using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Infrastructure.Persistence;
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

    [Fact]
    public async Task Every_model_path_counts_against_the_tenants_daily_total()
    {
        await using var model = await StubModelServer.StartAsync();
        // The classifier and the chunk context on, so a question, a scan and an ingestion each make more than one kind of model call.
        await using var instance = await StartAsync(model, new()
        {
            ["Assistant:Guard:Classifier"] = "Model",
            ["Assistant:Ingestion:ContextualRetrieval"] = "true",
        });
        var tenant = AssistantApp.NewTenant();
        var budget = instance.Services.GetRequiredService<ITenantTokenBudget>();
        async Task<long> UsedAsync() => (await budget.GetAsync(tenant, Ct)).Used;
        const int PerChatCall = StubModelServer.PromptTokens + StubModelServer.CompletionTokens;

        // A question: the classifier, the answer and the query embedding.
        var token = User(tenant);
        await AskOkAsync(instance.Client, token, await CreateAsync(instance.Client, token), Question);
        var afterQuestion = await UsedAsync();
        afterQuestion.ShouldBeGreaterThanOrEqualTo(2 * PerChatCall + 1, "classifier + answer + query embedding");

        // An invoice scan: the document classifier and the scan itself.
        using (var scan = await ScanAsync(instance.Client, AssistantApp.Token(tenant, "finance", "fiona"), Ct))
        {
            scan.StatusCode.ShouldBe(HttpStatusCode.OK, await scan.Content.ReadAsStringAsync(Ct));
        }

        var afterScan = await UsedAsync();
        (afterScan - afterQuestion).ShouldBeGreaterThanOrEqualTo(2 * PerChatCall, "document classifier + scan");

        // An ingestion (in the background, for the document's tenant): the document classifier, one context call per chunk, the embeddings.
        var admin = PolicyDocumentsApi.Admin(tenant);
        var document = await PolicyDocumentsApi.UploadOkAsync(instance.Client, admin, "policy.md", PolicyDocumentsApi.Markdown("counted", 2));
        await PolicyDocumentsApi.WaitForStatusAsync(instance.Client, admin, document.Id, "Ready");
        (await UsedAsync() - afterScan).ShouldBeGreaterThanOrEqualTo(3 * PerChatCall + 2, "document classifier + two chunk contexts + embeddings");
    }

    [Fact]
    public async Task Capped_tenant_cannot_upload_replace_or_scan()
    {
        await using var model = await StubModelServer.StartAsync();
        await using var instance = await StartAsync(model, new());
        var tenant = AssistantApp.NewTenant();
        var admin = PolicyDocumentsApi.Admin(tenant);
        var document = await PolicyDocumentsApi.UploadOkAsync(instance.Client, admin, "policy.md", PolicyDocumentsApi.Markdown("capped", 1));
        await PolicyDocumentsApi.WaitForStatusAsync(instance.Client, admin, document.Id, "Ready");
        var current = await PolicyDocumentsApi.GetOkAsync(instance.Client, admin, document.Id);

        // The tenant has used its day (on another instance, say).
        await instance.Services.GetRequiredService<ITenantTokenBudget>().AddAsync(tenant, 1_000_000_000, Ct);
        var calls = model.ChatRequests + model.EmbeddingRequests;

        using var upload = await PolicyDocumentsApi.UploadAsync(instance.Client, admin, "other.md", PolicyDocumentsApi.Markdown("other", 1));
        using var replace = await PolicyDocumentsApi.ReplaceAsync(
            instance.Client, admin, document.Id, current.RowVersion, "policy.md", PolicyDocumentsApi.Markdown("replaced", 1));
        using var scan = await ScanAsync(instance.Client, AssistantApp.Token(tenant, "finance", "fiona"), Ct);

        foreach (var response in new[] { upload, replace, scan })
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, body);
            body.ShouldContain(AnswerStreamer.TokenCapTitle);
            response.Headers.RetryAfter.ShouldNotBeNull();
        }

        (model.ChatRequests + model.EmbeddingRequests).ShouldBe(calls, "nothing reached the model");
        (await PolicyDocumentsApi.GetOkAsync(instance.Client, admin, document.Id)).Version.ShouldBe(1, "the replacement was not taken");
    }

    [Fact]
    public async Task Azure_content_filter_is_a_guard_block_not_a_failure()
    {
        var marker = $"zzfilter{Guid.NewGuid():N}"[..20];
        var logs = new CapturingSink();
        await using var model = await StubModelServer.StartAsync();
        await using var instance = await StartAsync(model, new(), services => services.AddSingleton<ILogEventSink>(logs));
        var token = User(AssistantApp.NewTenant());
        model.FailAnswers(request => StubModelError.ContentFilter($"Prompt: {request}"));

        var result = await AskAsync(instance.Client, token, await CreateAsync(instance.Client, token), $"{Question} {marker}");

        result.Status.ShouldBe(HttpStatusCode.OK);
        var error = result.Of("error").Single().Data;
        error.GetProperty("status").GetInt32().ShouldBe(422);
        error.GetProperty("title").GetString().ShouldBe(AnswerStreamer.GuardTitle);
        model.AnswerRequests.ShouldBe(1, "a refusal is not retried");
        var events = logs.Events;
        events.ShouldContain(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("content filter"));
        events.ShouldNotContain(e => e.Level >= LogEventLevel.Error, "a content filter refusal is not an error of ours");
        events.Select(Render).ShouldNotContain(line => line.Contains(marker) || line.Contains("content management policy"));
    }

    [Fact]
    public async Task Answer_the_client_left_mid_stream_still_counts_against_the_cap()
    {
        await using var model = await StubModelServer.StartAsync();
        await using var instance = await StartAsync(model, new());
        var tenant = AssistantApp.NewTenant();
        var token = User(tenant);
        var conversation = await CreateAsync(instance.Client, token);
        var budget = instance.Services.GetRequiredService<ITenantTokenBudget>();
        var outputLimit = instance.Services.GetRequiredService<IOptions<AiOptions>>().Value.Chat.MaxOutputTokens;
        // The answer streams its first text, then stalls: the usage (in the stream's last chunk) never comes.
        model.AnswerMode(StubAnswerMode.Stall);

        using (var request = AskRequest(token, conversation, Question))
        using (var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            using var response = await instance.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            await model.AnswerStalled.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            // The provider has billed the prompt and is producing the answer: the visitor closes the tab.
            await abort.CancelAsync();
        }

        // The stopped answer is saved once its model call has ended (and the counting stage has run).
        var stopped = await WaitForLastMessageAsync(instance.Client, token, conversation, m => m.GetProperty("role").GetString() == "Assistant");
        stopped.GetProperty("status").GetString().ShouldBe("Stopped");
        model.AnswerRequests.ShouldBe(1);

        // The reservation made before the answer call (prompt estimate + output limit) stays: the provider may have billed all of it.
        (await budget.GetAsync(tenant, Ct)).Used.ShouldBeGreaterThanOrEqualTo(outputLimit, "the cancelled answer is charged");
    }

    [Fact]
    public async Task Answer_cut_by_the_providers_output_filter_is_a_blocked_answer()
    {
        var logs = new CapturingSink();
        await using var model = await StubModelServer.StartAsync();
        await using var instance = await StartAsync(model, new(), services => services.AddSingleton<ILogEventSink>(logs));
        var tenant = AssistantApp.NewTenant();
        var token = User(tenant);
        var conversation = await CreateAsync(instance.Client, token);
        // Azure filters a completion by ending the stream with finish_reason content_filter (no error), then sends the usage.
        model.AnswerMode(StubAnswerMode.Filter);

        var result = await AskAsync(instance.Client, token, conversation, Question);

        result.Status.ShouldBe(HttpStatusCode.OK);
        var error = result.Of("error").Single().Data;
        error.GetProperty("status").GetInt32().ShouldBe(422);
        error.GetProperty("title").GetString().ShouldBe(AnswerStreamer.GuardTitle);
        result.Of("done").ShouldBeEmpty();
        model.AnswerRequests.ShouldBe(1, "a refusal is not retried");
        var saved = await WaitForLastMessageAsync(instance.Client, token, conversation, m => m.GetProperty("role").GetString() == "Assistant");
        saved.GetProperty("status").GetString().ShouldBe("Blocked");
        logs.Events.ShouldContain(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("content filter refused an answer"));
        logs.Events.ShouldNotContain(e => e.Level >= LogEventLevel.Error, "a content filter refusal is not an error of ours");
        // The stream was read to its end, so its usage still counts.
        (await instance.Services.GetRequiredService<ITenantTokenBudget>().GetAsync(tenant, Ct)).Used
            .ShouldBeGreaterThanOrEqualTo(StubModelServer.PromptTokens + StubModelServer.CompletionTokens);
    }

    [Fact]
    public async Task Capped_tenants_queued_upload_makes_no_model_calls_but_the_demo_seed_still_ingests()
    {
        await using var model = await StubModelServer.StartAsync();
        // Both seeded tenants have used their day before this instance starts, and the seed's ingestion is already queued. Globex's
        // document is made a user's upload, so it stands for any queued upload of a capped tenant; Acme's stays the demo seed's own.
        var (seeded, uploaded) = (DemoSeeder.Documents[0], DemoSeeder.Documents[1]);
        await CapSeededTenantsAsync(1);
        var budget = app.Services.GetRequiredService<ITenantTokenBudget>();
        var usedBefore = (await budget.GetAsync(seeded.TenantId, Ct)).Used;
        var cappedUsedBefore = (await budget.GetAsync(uploaded.TenantId, Ct)).Used;
        await using var instance = await StartAsync(
            model,
            new()
            {
                ["AI:DailyTokenCapPerTenant"] = "1",
                ["Assistant:Ingestion:ContextualRetrieval"] = "true",
            },
            beforeStart: database => MarkUploadedByAUserAsync(database, uploaded.DocumentId));

        var failed = await PolicyDocumentsApi.WaitForStatusAsync(
            instance.Client, PolicyDocumentsApi.Admin(uploaded.TenantId), uploaded.DocumentId, "Failed");
        failed.FailureReason.ShouldBe(PolicyIngestion.BudgetReachedReason);

        // The nightly reset deletes the policy and seeds it again: a tenant someone capped between midnight and the reset must not lose
        // its policy for the whole day. The seed is one fixed document, so letting it through bounds what it can spend.
        await PolicyDocumentsApi.WaitForStatusAsync(instance.Client, PolicyDocumentsApi.Admin(seeded.TenantId), seeded.DocumentId, "Ready");
        var usedAfter = (await budget.GetAsync(seeded.TenantId, Ct)).Used;
        usedAfter.ShouldBeGreaterThan(usedBefore, "the seed's ingestion still counts for its tenant");
        model.EmbeddingRequests.ShouldBeGreaterThan(0, "the seed's chunks were embedded");

        // The stub serves both tenants, so its counts cannot tell them apart; the capped tenant's own total can. Every model call of a
        // job is counted against its tenant (AiUsageScope), so an unchanged total proves the capped upload reached no model, not even
        // the document guard before a later per-batch stop. Read after the seed is Ready, so a late count would have landed by now.
        (await budget.GetAsync(uploaded.TenantId, Ct)).Used.ShouldBe(cappedUsedBefore, "the capped tenant's upload made no model call");
    }

    [Fact]
    public async Task Ingestion_stops_between_batches_once_the_cap_is_reached()
    {
        await using var model = await StubModelServer.StartAsync();
        // Cap 150: the first batch of 4 chunks (4 context calls of 60 tokens, 4 embedding tokens) passes it, so the second never starts.
        // The seeded tenants are capped already and their queued documents made users' uploads (the demo seed itself is not stopped by
        // the cap), so the stub sees this tenant's calls only.
        await CapSeededTenantsAsync(150);
        await using var instance = await StartAsync(
            model,
            new()
            {
                ["AI:DailyTokenCapPerTenant"] = "150",
                ["Assistant:Ingestion:ContextualRetrieval"] = "true",
            },
            beforeStart: async database =>
            {
                foreach (var (_, documentId) in DemoSeeder.Documents)
                {
                    await MarkUploadedByAUserAsync(database, documentId);
                }
            });
        foreach (var (seededTenant, documentId) in DemoSeeder.Documents)
        {
            await PolicyDocumentsApi.WaitForStatusAsync(instance.Client, PolicyDocumentsApi.Admin(seededTenant), documentId, "Failed");
        }

        var tenant = AssistantApp.NewTenant();
        var admin = PolicyDocumentsApi.Admin(tenant);

        var document = await PolicyDocumentsApi.UploadOkAsync(instance.Client, admin, "policy.md", PolicyDocumentsApi.Markdown("partway", 12));
        var failed = await PolicyDocumentsApi.WaitForStatusAsync(instance.Client, admin, document.Id, "Failed");

        failed.FailureReason.ShouldBe(PolicyIngestion.BudgetReachedReason);
        model.ChatRequests.ShouldBe(4, "the first batch's chunk contexts only (Assistant:Ingestion:BatchSize 4)");
        model.EmbeddingRequests.ShouldBe(1);
    }

    /// <summary>Makes a seeded document look like a user's upload (before the instance that ingests it starts).</summary>
    private static async Task MarkUploadedByAUserAsync(string database, Guid documentId) =>
        (await DemoResetProbe.CountAsync(
            database,
            $"with changed as (update policy_documents set uploaded_by = 'a-tenant-admin' where id = '{documentId}' returning 1) select count(*) from changed"))
        .ShouldBe(1);

    /// <summary>Puts the demo seed's tenants at <paramref name="tokens"/> today (on the shared Redis, whose cap of 200 000 they stay under).</summary>
    private async Task CapSeededTenantsAsync(long tokens)
    {
        // The shared app ingests its own copy of the seed when it starts, counted against the same tenants on the same Redis: wait until it
        // is done, or its counts land after the totals below were read (the test then failed when it ran first).
        foreach (var (seededTenant, documentId) in DemoSeeder.Documents)
        {
            await PolicyDocumentsApi.WaitForStatusAsync(app.Client, PolicyDocumentsApi.Admin(seededTenant), documentId, "Ready");
        }

        var budget = app.Services.GetRequiredService<ITenantTokenBudget>();
        foreach (var tenant in DemoSeeder.Documents.Select(d => d.TenantId).Distinct())
        {
            var used = (await budget.GetAsync(tenant, Ct)).Used;
            if (used < tokens)
            {
                await budget.AddAsync(tenant, tokens - used, Ct);
            }
        }
    }

    private static async Task<HttpResponseMessage> ScanAsync(HttpClient client, string token, CancellationToken cancellationToken)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync("Files/invoice-correct.pdf", cancellationToken));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "invoice.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/assistant/invoice-scans") { Content = form };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(request, cancellationToken);
    }

    private async Task<Instance> StartAsync(
        StubModelServer model, Dictionary<string, string?> settings, Action<IServiceCollection>? services = null, List<Activity>? spans = null,
        Func<string, Task>? beforeStart = null)
    {
        var databases = await app.Infra.CreateSeededDatabasesAsync($"model_{Guid.NewGuid():N}"[..20]);
        if (beforeStart is not null)
        {
            await beforeStart(databases.Database);
        }

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
