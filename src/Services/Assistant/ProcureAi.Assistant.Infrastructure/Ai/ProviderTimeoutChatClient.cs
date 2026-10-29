using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ProcureAi.Assistant.Infrastructure.Ai;

/// <summary>
/// The innermost stage of every chat pipeline: a provider that stops answering fails the call with a <see cref="TimeoutException"/>
/// after <c>AI:Chat:Timeout</c> (a transient provider failure, see <see cref="AiFailures"/>). A plain call may take that long in all; a
/// stream may wait that long for each next update (the first one included), so a long answer that keeps coming is never cut off. The SDK's
/// own network timeout covers the HTTP side; this one also covers a provider (or the Fake) that hangs without any network call. The
/// caller's own cancellation still surfaces as an <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class ProviderTimeoutChatClient(IChatClient inner, TimeSpan timeout) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await base.GetResponseAsync(messages, options, deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw TimedOut(exception);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var updates = base.GetStreamingResponseAsync(messages, options, deadline.Token).GetAsyncEnumerator(deadline.Token);
        while (true)
        {
            deadline.CancelAfter(timeout);
            bool more;
            try
            {
                more = await updates.MoveNextAsync();
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw TimedOut(exception);
            }

            if (!more)
            {
                yield break;
            }

            // The wait for the caller (who may be slow to take the update) does not count against the provider.
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            yield return updates.Current;
        }
    }

    private TimeoutException TimedOut(Exception inner) =>
        new($"The model provider did not answer within {timeout.TotalSeconds:0.#} s.", inner);
}
