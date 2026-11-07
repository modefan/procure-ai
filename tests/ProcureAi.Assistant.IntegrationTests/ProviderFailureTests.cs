using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Assistant.Application.Chat;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using static ProcureAi.Assistant.IntegrationTests.ChatApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// The real provider path (plan 08 Task 9): the Assistant on the <c>AzureFoundry</c> provider, configured only, against an OpenAI-compatible
/// stub (<see cref="StubModelServer"/>), so the OpenAI SDK's retries, the circuit breaker, the provider's error bodies and the daily token
/// cap are exercised as they will be against Azure. Each test runs an instance of its own on databases of its own: the shared app keeps
/// its breakers shut (MinimumThroughput 1000) and runs on the Fake.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class ProviderFailureTests(AssistantApp app)
{
    private const string Question = "Do purchases above R 100 000 need finance approval?";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StubModelError RateLimited() => new(
        429, StubModelError.ErrorBody("Rate limit reached for the deployment.", "rate_limit_exceeded"),
        new Dictionary<string, string> { ["retry-after-ms"] = "20", ["Retry-After"] = "0" });

    [Fact]
    public async Task Provider_429_is_retried_then_breaker_opens()
    {
        await using var model = await StubModelServer.StartAsync();
        await using var instance = await StartAsync(model, new()
        {
            ["AI:Resilience:ProviderMaxRetries"] = "1",
            ["AI:Resilience:MinimumThroughput"] = "3",
            ["AI:Resilience:FailureRatio"] = "0.5",
            ["AI:Resilience:BreakDuration"] = "00:05:00",
        });
        var token = User(AssistantApp.NewTenant());

        // One 429, then the SDK's own retry gets the answer: the user never sees the 429.
        model.EnqueueAnswerError(RateLimited());
        var answered = await AskOkAsync(instance.Client, token, await CreateAsync(instance.Client, token), Question);
        answered.StreamedText.ShouldContain("finance approval");
        model.AnswerRequests.ShouldBe(2, "the 429 and its retry");

        // The deployment keeps answering 429: each question is one call and its retry, then a 503 error event.
        model.FailAnswers(_ => RateLimited());
        for (var i = 0; i < 2; i++)
        {
            var failed = await AskAsync(instance.Client, token, await CreateAsync(instance.Client, token), Question);
            failed.Status.ShouldBe(HttpStatusCode.OK);
            failed.Of("error").Single().Data.GetProperty("status").GetInt32().ShouldBe(503);
        }

        model.AnswerRequests.ShouldBe(6);

        // Two failures in three calls: the breaker is open, and the next question fails at once without reaching the provider.
        var refused = await AskAsync(instance.Client, token, await CreateAsync(instance.Client, token), Question);
        var error = refused.Of("error").Single().Data;
        error.GetProperty("status").GetInt32().ShouldBe(503);
        error.GetProperty("title").GetString().ShouldBe(AnswerStreamer.UnavailableTitle);
        model.AnswerRequests.ShouldBe(6, "an open breaker refuses before the provider is asked");
    }

    [Fact]
    public async Task Provider_error_text_never_reaches_client_or_logs()
    {
        const string Secret = "sk-proj-Zx81LeakedKeyValue0123456789abcdefABCDEF";
        var marker = $"zzmarker{Guid.NewGuid():N}"[..20];
        var logs = new CapturingSink();
        var spans = new List<Activity>();
        await using var model = await StubModelServer.StartAsync();
        await using var instance = await StartAsync(
            model, new() { ["Serilog:MinimumLevel:Default"] = "Verbose" }, services => services.AddSingleton<ILogEventSink>(logs), spans);
        var token = User(AssistantApp.NewTenant());

        // The provider's error bodies echo a key and the prompt (with the user's question), as real providers' bodies can.
        string Echo(string body) => $"Request failed for key {Secret}; the prompt was: {body}";
        var bodies = new List<string>();
        foreach (var (status, code) in new[] { (500, "server_error"), (400, "invalid_request_error") })
        {
            model.FailAnswers(request => new StubModelError(status, StubModelError.ErrorBody(Echo(request), code)));
            var result = await AskAsync(instance.Client, token, await CreateAsync(instance.Client, token), $"{Question} {marker}");
            result.Status.ShouldBe(HttpStatusCode.OK);
            result.Of("error").Single().Data.GetProperty("status").GetInt32().ShouldBe(status == 500 ? 503 : 500);
            bodies.Add(string.Join("\n", result.Frames.Select(f => f.Data.GetRawText())));
        }

        model.AnswerRequests.ShouldBeGreaterThanOrEqualTo(2);
        foreach (var body in bodies)
        {
            body.ShouldNotContain(Secret);
            body.ShouldNotContain(marker);
            body.ShouldNotContain("the prompt was");
        }

        var logged = logs.Events.Select(Render).ToList();
        logged.ShouldContain(line => line.Contains("HTTP 500 (server_error)"), "the failure itself is logged, by status and code");
        logged.ShouldNotContain(line => line.Contains(Secret), "no provider key-like text in any log");
        logged.ShouldNotContain(line => line.Contains("the prompt was"), "no provider error text in any log");
        logged.ShouldNotContain(line => line.Contains(marker), "no prompt text in any log");

        List<string> traced;
        lock (spans)
        {
            traced = [.. spans.SelectMany(SpanTexts)];
        }

        traced.ShouldNotContain(text => text.Contains(Secret) || text.Contains("the prompt was") || text.Contains(marker));
    }

    [Fact]
    public async Task Daily_token_cap_stops_further_answers_with_a_friendly_message()
    {
        await using var model = await StubModelServer.StartAsync();
        // One answer (40 + 20 tokens) passes a cap of 50, so the next question of the tenant is refused.
        await using var instance = await StartAsync(model, new() { ["AI:DailyTokenCapPerTenant"] = "50" });
        var tenant = AssistantApp.NewTenant();
        var token = User(tenant);
        var conversation = await CreateAsync(instance.Client, token);

        await AskOkAsync(instance.Client, token, conversation, Question);
        var budget = await instance.Services.GetRequiredService<ITenantTokenBudget>().GetAsync(tenant, Ct);
        budget.Used.ShouldBeGreaterThanOrEqualTo(StubModelServer.PromptTokens + StubModelServer.CompletionTokens);
        var calls = model.AnswerRequests;

        var refused = await AskAsync(instance.Client, token, conversation, "And for laptops?");

        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Body.ShouldContain(AnswerStreamer.TokenCapTitle);
        refused.Body.ShouldContain("after midnight");
        model.AnswerRequests.ShouldBe(calls, "no model call once the cap is reached");
        (await GetOkAsync(instance.Client, ChatApi.Quota, token)).GetProperty("used").GetInt32().ShouldBe(1, "a refused question costs no quota");
        // Another user of the tenant is refused too (the cap is the tenant's), another tenant is not.
        var colleague = User(tenant, "sam");
        (await AskAsync(instance.Client, colleague, await CreateAsync(instance.Client, colleague), Question)).Status.ShouldBe(HttpStatusCode.TooManyRequests);
        var other = User(AssistantApp.NewTenant());
        await AskOkAsync(instance.Client, other, await CreateAsync(instance.Client, other), Question);
    }

    private async Task<Instance> StartAsync(
        StubModelServer model, Dictionary<string, string?> settings, Action<IServiceCollection>? services = null, List<Activity>? spans = null)
    {
        var databases = await app.Infra.CreateSeededDatabasesAsync($"model_{Guid.NewGuid():N}"[..20]);
        foreach (var (key, value) in model.Settings)
        {
            settings.TryAdd(key, value);
        }

        var factory = app.CreateInstance(databases, settings, services, spans);
        return new Instance(factory, factory.CreateClient());
    }

    private sealed class Instance(AssistantApiFactory factory, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public IServiceProvider Services => factory.Services;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
        }
    }

    private static string Render(LogEvent logEvent) =>
        string.Join(
            " | ",
            [logEvent.RenderMessage(), logEvent.Exception?.ToString() ?? "", .. logEvent.Properties.Select(p => $"{p.Key}={p.Value}")]);

    private static IEnumerable<string> SpanTexts(Activity activity) =>
        [
            activity.StatusDescription ?? "",
            .. activity.TagObjects.Select(t => t.Value?.ToString() ?? ""),
            .. activity.Events.SelectMany(e => e.Tags.Select(t => t.Value?.ToString() ?? "")),
        ];

    /// <summary>Every Serilog event of the instance (registered in DI, so <c>ReadFrom.Services</c> adds it).</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
            {
                _events.Add(logEvent);
            }
        }
    }
}
