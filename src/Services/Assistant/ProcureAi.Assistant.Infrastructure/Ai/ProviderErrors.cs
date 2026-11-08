using System.ClientModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ProcureAi.Assistant.Application.Guardrails;

namespace ProcureAi.Assistant.Infrastructure.Ai;

/// <summary>
/// Keeps the provider's error text inside the provider stage. The OpenAI SDK puts the whole error body into the
/// <see cref="ClientResultException"/> message, and providers echo parts of the request there (the prompt, a parameter, sometimes a
/// key-like value). That message would reach every log line, span status and retry event that prints the exception. So an error answer
/// is thrown on as a new <see cref="ClientResultException"/> with the same response (its <see cref="ClientResultException.Status"/>, so
/// <see cref="AiFailures"/> and the circuit breaker see the same failure) and a message of our own: the HTTP status and the error code,
/// when the code is a plain identifier (<c>rate_limit_exceeded</c>, <c>unsupported_value</c>). The original is not kept as the inner
/// exception, since logs print inner exceptions too (a failed connection keeps only its <see cref="HttpRequestError"/>). A content filter refusal becomes a <see cref="GuardBlockedException"/>.
/// </summary>
internal static partial class ProviderErrors
{
    /// <summary>The provider's content filter code (Azure OpenAI: a 400 with <c>error.code</c> <c>content_filter</c>).</summary>
    public const string ContentFilterCode = "content_filter";

    /// <summary>
    /// The exception to throw instead of <paramref name="exception"/>: a 400 from the provider's content filter is a guard block
    /// (<see cref="GuardReasons.ProviderFilter"/>, input stage: the friendly 422, never a failure); anything else the same status and
    /// response, without the provider's text.
    /// </summary>
    public static Exception Scrub(ClientResultException exception)
    {
        if (exception.Status == 400 && exception.GetRawResponse() is { } filtered && CodeOf(filtered.Content) == ContentFilterCode)
        {
            return new GuardBlockedException(GuardBlockedException.InputStage, GuardReasons.ProviderFilter);
        }

        var response = exception.GetRawResponse();
        var status = exception.Status;
        var code = response is null ? null : CodeOf(response.Content);
        var message = code is null
            ? string.Create(CultureInfo.InvariantCulture, $"The model provider answered HTTP {status}.")
            : string.Create(CultureInfo.InvariantCulture, $"The model provider answered HTTP {status} ({code}).");
        // No answer at all (status 0): keep only the kind of network failure (a message of our own), so the counting stage can tell a
        // connection that was never made (nothing billed) from one that broke after the request was sent.
        var network = response is null && exception.InnerException is HttpRequestException { HttpRequestError: var error }
            ? new HttpRequestException(error, string.Create(CultureInfo.InvariantCulture, $"The connection to the model provider failed ({error})."))
            : null;
        return new ClientResultException(message, response, network);
    }

    /// <summary>The error code of an OpenAI-style error body (<c>error.code</c>, else <c>error.type</c>) when it is a plain identifier.</summary>
    internal static string? CodeOf(BinaryData? body)
    {
        if (body is null || body.ToMemory().IsEmpty)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body.ToMemory());
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in new[] { "code", "type" })
            {
                if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } text && PlainCode().IsMatch(text))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>A code we may log: lower-case letters, digits and underscores, at most 40 (never free text, never a key).</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainCode();
}

/// <summary>
/// The innermost stage over the provider's chat client: <see cref="ProviderErrors"/>, and the provider's content filter on the answer
/// itself. Azure filters a completion by ending it normally with <c>finish_reason: content_filter</c> (the text cut short or empty), not
/// with an error; such a call is a guard block at the output stage (<see cref="GuardReasons.ProviderFilter"/>), so the cut answer is never
/// kept as a normal one. A stream is read to its end first, so the usage in its last update still reaches the counting stage.
/// </summary>
internal sealed class ProviderErrorChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (ClientResultException exception)
        {
            throw ProviderErrors.Scrub(exception);
        }

        return response.FinishReason == ChatFinishReason.ContentFilter ? throw OutputFiltered() : response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var filtered = false;
        while (true)
        {
            bool more;
            try
            {
                more = await updates.MoveNextAsync();
            }
            catch (ClientResultException exception)
            {
                throw ProviderErrors.Scrub(exception);
            }

            if (!more)
            {
                break;
            }

            var update = updates.Current;
            if (update.FinishReason == ChatFinishReason.ContentFilter)
            {
                filtered = true;
            }

            // After the filter only what carries no answer text is passed on (the usage), so nothing filtered reaches the client.
            if (!filtered || string.IsNullOrEmpty(update.Text))
            {
                yield return update;
            }
        }

        if (filtered)
        {
            throw OutputFiltered();
        }
    }

    private static GuardBlockedException OutputFiltered() => new(GuardBlockedException.OutputStage, GuardReasons.ProviderFilter);
}

/// <summary>The innermost stage over the provider's embedding generator: <see cref="ProviderErrors"/>.</summary>
internal sealed class ProviderErrorEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GenerateAsync(values, options, cancellationToken);
        }
        catch (ClientResultException exception)
        {
            throw ProviderErrors.Scrub(exception);
        }
    }
}
