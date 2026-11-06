using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Infrastructure.Ai;
using ProcureAi.Assistant.Infrastructure.Ai.Fake;
using Shouldly;

namespace ProcureAi.Assistant.Tests;

/// <summary>
/// The breaker's edge cases under the caller's cancellation (plan 05 ruling 60, plan 07 carry-over 19): whatever a provider throws
/// once the caller has cancelled is the caller's doing and never counts, and a half-open trial call the caller cancelled proves
/// nothing, so it never closes the breaker.
/// </summary>
public sealed class ModelCircuitBreakerTests
{
    private static readonly TimeSpan BreakDuration = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(ScriptedProvider chat)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AI:Provider"] = "Fake",
            ["AI:Chat:Model"] = "gpt-4.1-mini",
            ["AI:Chat:MaxOutputTokens"] = "800",
            ["AI:Embedding:Model"] = "text-embedding-3-small",
            ["AI:Embedding:Dimensions"] = "1536",
            ["AI:Fake:StreamDelayMs"] = "0",
            ["Assistant:Tools:MaxCallsPerQuestion"] = "5",
            ["Assistant:Quota:QuestionsPerDay"] = "50",
            ["AI:Resilience:MinimumThroughput"] = "3",
            ["AI:Resilience:FailureRatio"] = "0.5",
            ["AI:Resilience:BreakDuration"] = BreakDuration.ToString(),
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAssistantAi(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton(new ProviderClients(chat, new FakeEmbeddingGenerator()));
        return services.BuildServiceProvider();
    }

    private static Task<ChatResponse> AskAsync(IChatClient client, CancellationToken token) =>
        client.GetResponseAsync([new(ChatRole.User, "hello")], cancellationToken: token);

    [Fact]
    public void A_provider_abort_after_the_caller_cancelled_does_not_count()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var aborted = new HttpRequestException("connection aborted", null, System.Net.HttpStatusCode.ServiceUnavailable);

        ModelCircuitBreaker.Counts(aborted, cancelled.Token).ShouldBeFalse("the caller hung up first: not the provider's fault");
        ModelCircuitBreaker.Counts(aborted, CancellationToken.None).ShouldBeTrue("the same failure without a cancelled caller counts");
    }

    [Fact]
    public async Task Provider_aborts_under_caller_cancellation_never_open_the_breaker()
    {
        var chat = new ScriptedProvider();
        await using var provider = Build(chat);
        var client = provider.GetRequiredKeyedService<IChatClient>(AiClients.Utility);

        // The caller cancels while the call runs, and the provider's SDK surfaces it as a transport failure, not as a cancellation.
        for (var i = 0; i < 5; i++)
        {
            using var caller = new CancellationTokenSource();
            chat.Next = token => { caller.Cancel(); throw new HttpRequestException("aborted", null, System.Net.HttpStatusCode.BadGateway); };
            await Should.ThrowAsync<HttpRequestException>(() => AskAsync(client, caller.Token));
        }

        chat.Next = _ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        (await AskAsync(client, Ct)).Text.ShouldBe("ok", "five caller cancellations left the breaker closed");
    }

    [Fact]
    public async Task Cancelled_half_open_probe_does_not_close_the_breaker()
    {
        var chat = new ScriptedProvider();
        await using var provider = Build(chat);
        var client = provider.GetRequiredKeyedService<IChatClient>(AiClients.Utility);

        chat.Next = _ => throw new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);
        for (var i = 0; i < 3; i++)
        {
            await Should.ThrowAsync<HttpRequestException>(() => AskAsync(client, Ct));
        }

        await Should.ThrowAsync<Polly.CircuitBreaker.BrokenCircuitException>(() => AskAsync(client, Ct));
        await Task.Delay(BreakDuration * 2, Ct);

        // The trial call: the user cancels it before the provider answers.
        using (var caller = new CancellationTokenSource())
        {
            chat.Next = token => { caller.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(new ChatResponse()); };
            await Should.ThrowAsync<OperationCanceledException>(() => AskAsync(client, caller.Token));
        }

        // The provider is still down. Had the cancelled probe closed the breaker, this call would reach it.
        chat.Next = _ => throw new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);
        var calls = chat.Calls;
        await Should.ThrowAsync<Polly.CircuitBreaker.BrokenCircuitException>(() => AskAsync(client, Ct));
        chat.Calls.ShouldBe(calls, "the breaker stayed open: the cancelled probe proved nothing");

        // After the next break a real trial call closes it as usual.
        await Task.Delay(BreakDuration * 2, Ct);
        chat.Next = _ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "back")));
        (await AskAsync(client, Ct)).Text.ShouldBe("back");
        (await AskAsync(client, Ct)).Text.ShouldBe("back", "the successful probe closed the breaker");
    }

    /// <summary>A provider whose next answer the test sets.</summary>
    private sealed class ScriptedProvider() : DelegatingChatClient(new FakeChatClient())
    {
        public int Calls;

        public Func<CancellationToken, Task<ChatResponse>> Next { get; set; } = _ => Task.FromResult(new ChatResponse());

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Next(cancellationToken);
        }
    }
}
