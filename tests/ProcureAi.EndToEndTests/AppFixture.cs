using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// One AppHost (the whole system) and one headless Chromium for the whole run. Secrets are read from the same places
/// the AppHost uses (its user secrets, and the user environment variable DEMO_ADMIN_API_KEY) and never printed.
/// If they are missing the tests skip with a message instead of failing: CI wiring comes with the pipeline task.
/// <para>
/// External target (plan 07 Task 8): with <c>PROCUREAI_E2E_GATEWAY_URL</c> set (e.g. <c>https://procure.localtest.me</c>, the k3d cluster) no
/// AppHost starts; the tests run against that deployment, Keycloak at <c>PROCUREAI_E2E_KEYCLOAK_URL</c> (default: <c>https://auth.</c> + the
/// gateway's host). The demo password still comes from the AppHost user secrets (the cluster's Secrets are made from them). Tests that need
/// the AppHost itself (its resource commands, its settings) carry <c>[Trait("Target", "AppHost")]</c>; run the rest with
/// <c>dotnet run --project tests/ProcureAi.EndToEndTests -- -trait- "Target=AppHost"</c> (the xUnit v3 runner's option; <c>--filter-not-trait</c> is Microsoft Testing Platform syntax, which this project does not use).
/// </para>
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    /// <summary>The deployment to test instead of starting the AppHost (external target mode).</summary>
    public const string GatewayUrlVariable = "PROCUREAI_E2E_GATEWAY_URL";

    /// <summary>Keycloak's public URL in external target mode (optional).</summary>
    public const string KeycloakUrlVariable = "PROCUREAI_E2E_KEYCLOAK_URL";

    /// <summary>
    /// Optional: a folder to record a video of every browser page into (<c>&lt;test&gt;-&lt;variant&gt;.&lt;start&gt;-&lt;end&gt;.webm</c>, Unix
    /// milliseconds of the page's start and the context's end, so the pages of several people can be cut into one story). The README's
    /// full-flow GIF is made from these (docs/readme-assets.md).
    /// </summary>
    public const string VideoDirectoryVariable = "PROCUREAI_E2E_VIDEO_DIR";

    /// <summary>Optional: milliseconds Playwright waits before each browser action (Playwright's SlowMo), so a recorded video can be followed.</summary>
    public const string SlowMoVariable = "PROCUREAI_E2E_SLOW_MO_MS";

    private static BrowserTypeLaunchOptions LaunchOptions() => new()
    {
        Headless = true,
        SlowMo = float.TryParse(Environment.GetEnvironmentVariable(SlowMoVariable), System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : null,
    };

    /// <summary>The kubectl context of the external target, for the tests that change data behind the app's back (default k3d-procure).</summary>
    public const string KubeContextVariable = "PROCUREAI_E2E_KUBE_CONTEXT";

    /// <summary>The AppHost's UserSecretsId (src/AppHost/ProcureAi.AppHost): where the demo password lives.</summary>
    private const string AppHostUserSecretsId = "procure-ai-apphost";

    /// <summary>
    /// External target only: the user-secrets store holding that deployment's demo password (default the AppHost's; the hosted demo's is
    /// <c>procure-ai-hosted</c>, the GitOps repo's bootstrap/new-secrets.ps1 -Environment hosted). Its admin key comes from DEMO_ADMIN_API_KEY
    /// in the test process's environment.
    /// </summary>
    public const string UserSecretsIdVariable = "PROCUREAI_E2E_USER_SECRETS_ID";

    public const string TenantId = "acme";
    public const string DemoUser = "tom@acme.test";

    /// <summary>The Keycloak user IDs of the demo realm (infra/keycloak): the admin key acts on behalf of them.</summary>
    public const string TomId = "00000000-0000-4000-8000-000000000001";
    public const string MariaId = "00000000-0000-4000-8000-000000000002";

    /// <summary>The Fake model's pause between streamed words in these tests (AppHost <c>AI:Fake:StreamDelayMs</c>).</summary>
    public const int FakeStreamDelayMs = 60;

    // The first start pulls images and runs npm install for the Angular app.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(12);

    private DistributedApplication? _app;
    private IPlaywright? _playwright;
    private string? _demoPassword;
    private string? _adminApiKey;
    private string? _skipReason;

    public Uri GatewayUrl { get; private set; } = null!;
    public Uri KeycloakUrl { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public string DemoPassword => _demoPassword!;

    /// <summary>True when the tests run against a deployment (<see cref="GatewayUrlVariable"/>) instead of an AppHost they started.</summary>
    public bool IsExternal { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _adminApiKey = Environment.GetEnvironmentVariable("DEMO_ADMIN_API_KEY")
                       ?? Environment.GetEnvironmentVariable("DEMO_ADMIN_API_KEY", EnvironmentVariableTarget.User);

        var externalGateway = Environment.GetEnvironmentVariable(GatewayUrlVariable);
        if (!string.IsNullOrWhiteSpace(externalGateway))
        {
            await InitializeExternalAsync(externalGateway);
            return;
        }

        // Tests never keep data: no volumes, so every run starts from empty databases. The Fake model streams one word every 60 ms
        // (instead of 15), so Stop_keeps_partial_answer can stop an answer halfway.
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ProcureAi_AppHost>(
            ["Persistence:UseDataVolumes=false", $"AI:Fake:StreamDelayMs={FakeStreamDelayMs}"]);
        _demoPassword = builder.Configuration["Parameters:keycloak-demo-password"];

        if (string.IsNullOrEmpty(_adminApiKey) || string.IsNullOrEmpty(_demoPassword))
        {
            _skipReason = "End-to-end tests need the user environment variable DEMO_ADMIN_API_KEY and the AppHost user secret "
                          + "Parameters:keycloak-demo-password (see DEMO PASSWORDS.txt). Not set, so the tests are skipped.";
            return;
        }

        // Fail fast, before anything expensive is started: Chromium is downloaded once per machine and Playwright
        // version (a no-op afterwards), then launched.
        var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exit != 0)
        {
            throw new InvalidOperationException($"playwright install chromium failed with exit code {exit}");
        }
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(LaunchOptions());

        // xUnit does not reliably call DisposeAsync after a failed InitializeAsync, so clean up here and rethrow.
        try
        {
            _app = await builder.BuildAsync();
            await _app.StartAsync();

            using var cts = new CancellationTokenSource(StartupTimeout);
            await _app.ResourceNotifications.WaitForResourceHealthyAsync("gateway", cts.Token);
            // The demo data seed (plan 06 Task 15) runs once the gateway is up; the tests start after it, on the seeded data (the scan
            // test needs its PO-1001), and nothing of the seed changes under a test.
            var seeded = await _app.ResourceNotifications.WaitForResourceAsync(
                "demo-data", e => KnownResourceStates.TerminalStates.Contains(e.Snapshot.State?.Text), cts.Token);
            if (seeded.Snapshot.ExitCode != 0)
            {
                throw new InvalidOperationException($"The demo data seed failed (exit code {seeded.Snapshot.ExitCode}): see the demo-data resource's log.");
            }

            GatewayUrl = new Uri(_app.GetEndpoint("gateway", "https").ToString());
            KeycloakUrl = new Uri(_app.GetEndpoint("keycloak", "http").ToString());
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// External target: no AppHost. The deployment must be up and seeded (on the k3d cluster Argo CD's demo-seed hook has run); the fixture
    /// only waits for the gateway's readiness, so a deployment that is still starting does not fail every test.
    /// </summary>
    private async Task InitializeExternalAsync(string gatewayUrl)
    {
        IsExternal = true;
        GatewayUrl = new Uri(gatewayUrl.TrimEnd('/') + "/");
        var keycloak = Environment.GetEnvironmentVariable(KeycloakUrlVariable);
        KeycloakUrl = new Uri(string.IsNullOrWhiteSpace(keycloak) ? $"{GatewayUrl.Scheme}://auth.{GatewayUrl.Authority}" : keycloak);
        var secretsId = Environment.GetEnvironmentVariable(UserSecretsIdVariable) is { Length: > 0 } id ? id : AppHostUserSecretsId;
        _demoPassword = new ConfigurationBuilder()
            .AddUserSecrets(secretsId)
            .Build()["Parameters:keycloak-demo-password"];

        if (string.IsNullOrEmpty(_adminApiKey) || string.IsNullOrEmpty(_demoPassword))
        {
            _skipReason = "End-to-end tests need the user environment variable DEMO_ADMIN_API_KEY and the AppHost user secret "
                          + "Parameters:keycloak-demo-password (see DEMO PASSWORDS.txt). Not set, so the tests are skipped.";
            return;
        }

        var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exit != 0)
        {
            throw new InvalidOperationException($"playwright install chromium failed with exit code {exit}");
        }
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(LaunchOptions());

        try
        {
            // The local cluster's certificate comes from its own CA (and ESET re-signs TLS on this PC): no certificate check, as the
            // browser contexts (IgnoreHTTPSErrors).
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
            {
                BaseAddress = GatewayUrl,
                Timeout = TimeSpan.FromSeconds(10),
            };
            await EventuallyAsync<bool>(
                async () =>
                {
                    try
                    {
                        using var response = await http.GetAsync("health/ready");
                        return response.IsSuccessStatusCode ? true : null;
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                    {
                        throw new InvalidOperationException(e.Message, e);
                    }
                },
                $"{GatewayUrl}health/ready", TimeSpan.FromMinutes(2));
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// For a test that needs the AppHost itself (its resource commands or settings only the test AppHost has): skips it in external
    /// target mode with <paramref name="reason"/>. Such tests also carry <c>[Trait("Target", "AppHost")]</c>, so a filtered run leaves
    /// them out altogether.
    /// </summary>
    public void RequireAppHost(string reason)
    {
        RequireReady();
        if (IsExternal)
        {
            Assert.Skip($"Needs the AppHost: {reason}");
        }
    }

    /// <summary>
    /// Skips the calling test (with the reason) when secrets are missing, unless REQUIRE_E2E=1 (or true), in which
    /// case a missing secret is a failure. CI sets it so that a misconfigured pipeline cannot go green by skipping.
    /// </summary>
    public void RequireReady()
    {
        if (_skipReason is null)
        {
            return;
        }
        var require = Environment.GetEnvironmentVariable("REQUIRE_E2E");
        if (require is "1" || string.Equals(require, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("REQUIRE_E2E is set but the end-to-end prerequisites are missing. " + _skipReason);
        }
        Assert.Skip(_skipReason);
    }

    /// <summary>Where traces and screenshots go; the CI workflow uploads this folder when the run fails.</summary>
    public static readonly string ArtifactsDirectory = Path.Combine(AppContext.BaseDirectory, "playwright-artifacts");

    /// <summary>A file-name-safe version of a test name plus its theory arguments.</summary>
    public static string ArtifactName(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"[^\w.-]+", "_").Trim('_');

    /// <summary>
    /// A fresh, isolated browser context (own cookies) that records a Playwright trace. On dispose it saves the trace
    /// (<c>&lt;test&gt;[-&lt;variant&gt;].zip</c>) and a screenshot of every open page (<c>&lt;test&gt;-pageN.png</c>) into <see cref="ArtifactsDirectory"/>.
    /// A test that fails inside <c>await using</c> disposes while unwinding, so the screenshot shows the failing state.
    /// The gateway is https with the ASP.NET dev certificate.
    /// </summary>
    public async Task<TracedContext> NewContextAsync(
        string? variant = null, [System.Runtime.CompilerServices.CallerMemberName] string testName = "test")
    {
        // Theory cases share one method name, so their traces and screenshots need the arguments in the name.
        testName = ArtifactName(variant is null ? testName : $"{testName}-{variant}");
        var options = new BrowserNewContextOptions { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1280, Height = 800 } };
        var videoDirectory = Environment.GetEnvironmentVariable(VideoDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(videoDirectory))
        {
            options.RecordVideoDir = Path.Combine(videoDirectory, "raw");
            options.RecordVideoSize = new() { Width = 1280, Height = 800 };
        }
        var context = await Browser.NewContextAsync(options);
        await context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true, Sources = false });
        return new TracedContext(context, testName, string.IsNullOrWhiteSpace(videoDirectory) ? null : videoDirectory);
    }

    /// <summary>
    /// Creates a draft purchase request on IT-200 through the gateway with the admin API key; returns its number. IT-200 is Tom's cost center:
    /// a requester's All list shows only the requests of his own cost centers (plan 07 ruling 12, Task 0c), so a request on CC-100 is not
    /// in Tom's list.
    /// </summary>
    public async Task<string> SeedRequestAsync(string title)
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = GatewayUrl,
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/requests")
        {
            Content = JsonContent.Create(new
            {
                title,
                costCenterCode = "IT-200",
                supplierId = "contoso-hardware",
                lines = new[] { new { sku = "LAP-14", quantity = 3 } },
            }),
        };
        message.Headers.Add("X-Api-Key", _adminApiKey);
        message.Headers.Add("X-Tenant-Id", TenantId);
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await http.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Seeding failed: {(int)response.StatusCode} {body}");
        }
        using var json = System.Text.Json.JsonDocument.Parse(body);
        return json.RootElement.GetProperty("number").GetString()!;
    }

    /// <summary>
    /// Creates a request of one catalog item from contoso-hardware on IT-200 for Tom and submits it (the admin key, on behalf of Tom), so it
    /// is waiting for Maria. MON-27 x 1 is R 4 830,00 incl. VAT: one approval step, by the manager.
    /// </summary>
    public async Task<(Guid Id, string Number)> SubmitRequestAsync(string title, string sku = "MON-27", int quantity = 1)
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = GatewayUrl,
        };

        async Task<(System.Text.Json.JsonDocument Body, string? ETag)> SendAsync(string path, object? body, string? ifMatch, System.Net.HttpStatusCode expected)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path);
            message.Headers.Add("X-Api-Key", _adminApiKey);
            message.Headers.Add("X-Tenant-Id", TenantId);
            message.Headers.Add("X-On-Behalf-Of-User", TomId);
            message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            if (ifMatch is not null)
            {
                message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }
            if (body is not null)
            {
                message.Content = JsonContent.Create(body);
            }
            using var response = await http.SendAsync(message);
            var text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != expected)
            {
                throw new InvalidOperationException($"POST {path}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
            }
            return (System.Text.Json.JsonDocument.Parse(text), response.Headers.ETag?.ToString());
        }

        var (created, tag) = await SendAsync("/api/v1/requests", new
        {
            title,
            costCenterCode = "IT-200",
            supplierId = "contoso-hardware",
            lines = new[] { new { sku, quantity } },
        }, null, System.Net.HttpStatusCode.Created);
        var id = created.RootElement.GetProperty("id").GetGuid();
        var number = created.RootElement.GetProperty("number").GetString()!;
        _submitted.Add(id);
        (await SendAsync($"/api/v1/requests/{id}/submit", null, tag, System.Net.HttpStatusCode.Accepted)).Body.Dispose();
        created.Dispose();
        return (id, number);
    }

    /// <summary>
    /// Calls an API through the gateway with the admin key, on behalf of <paramref name="onBehalfOf"/> when given (POSTs and PUTs get
    /// an Idempotency-Key) in <paramref name="tenant"/>; fails unless the status is <paramref name="expected"/>. Returns the JSON body (undefined when empty) and the ETag.
    /// </summary>
    public async Task<(System.Text.Json.JsonElement Body, string? ETag)> CallAsync(
        HttpMethod method, string path, string? onBehalfOf, object? body = null, string? ifMatch = null,
        System.Net.HttpStatusCode expected = System.Net.HttpStatusCode.OK, string tenant = TenantId)
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = GatewayUrl,
        };
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Add("X-Api-Key", _adminApiKey);
        message.Headers.Add("X-Tenant-Id", tenant);
        if (onBehalfOf is not null)
        {
            message.Headers.Add("X-On-Behalf-Of-User", onBehalfOf);
        }
        if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }
        if (ifMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }
        using var response = await http.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException($"{method} {path}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        }
        var json = text.Length == 0 ? default : System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        return (json, response.Headers.ETag?.ToString());
    }

    /// <summary>
    /// Polls <paramref name="read"/> every second until it returns a value (not null) or <paramref name="timeout"/> passes; a failed read
    /// counts as "not yet" (a 404 before something exists, a 503 while a service starts).
    /// </summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, string what, TimeSpan timeout)
        where T : struct
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await read() is { } value)
                {
                    return value;
                }
            }
            catch (InvalidOperationException e)
            {
                last = e;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}. {last?.Message}");
    }

    /// <summary>Waits until the seeded acme procurement policy is ingested (the assistant answers from it).</summary>
    public Task WaitForSeededPolicyAsync() =>
        EventuallyAsync<bool>(
            async () => (await CallAsync(HttpMethod.Get, $"/api/v1/assistant/policy-documents/{AcmePolicyId}", null)).Body
                .GetProperty("status").GetString() == "Ready" ? true : null,
            "the seeded policy to be Ready", TimeSpan.FromMinutes(3));

    /// <summary>The acme copy of the seeded procurement policy (the Assistant's DemoSeeder).</summary>
    public static readonly Guid AcmePolicyId = Guid.Parse("5eed0000-0000-4000-8000-00000000a001");

    /// <summary>Maria approves the open step of the request in her inbox (the admin key on behalf of Maria).</summary>
    public async Task ApproveAsMariaAsync(Guid requestId)
    {
        var step = await EventuallyAsync<System.Text.Json.JsonElement>(
            async () => (await CallAsync(HttpMethod.Get, "/api/v1/approvals/inbox?pageSize=100", MariaId)).Body.GetProperty("items")
                .EnumerateArray().Where(s => s.GetProperty("requestId").GetGuid() == requestId).Select(s => (System.Text.Json.JsonElement?)s)
                .FirstOrDefault(),
            "the request in Maria's inbox", TimeSpan.FromSeconds(60));
        await CallAsync(
            HttpMethod.Post, $"/api/v1/approvals/{step.GetProperty("id").GetGuid()}/approve", MariaId,
            ifMatch: $"\"{step.GetProperty("version").GetUInt32()}\"");
    }

    /// <summary>Waits until the request's purchase order exists (as Tom sees it) and returns its ID and number.</summary>
    public async Task<(Guid Id, string Number)> WaitForOrderAsync(Guid requestId)
    {
        var order = await EventuallyAsync<System.Text.Json.JsonElement>(
            async () => (await CallAsync(HttpMethod.Get, $"/api/v1/purchasing/requests/{requestId}/order", TomId)).Body,
            "the request's purchase order", TimeSpan.FromSeconds(90));
        return (order.GetProperty("id").GetGuid(), order.GetProperty("number").GetString()!);
    }

    /// <summary>Signs <paramref name="page"/> in with the welcome page's demo button for <paramref name="key"/> (see <see cref="Pages.DemoRoles"/>).</summary>
    public Task DemoLogInAsync(IPage page, string key, string? path = null) => Pages.DemoLogInAsync(this, page, key, path);

    /// <summary>The demo data scenarios the AppHost seeded (the files the seeder ships), to find a seeded request by its scenario key.</summary>
    public static IReadOnlyList<ProcureAi.DemoData.Scenario> Scenarios { get; } =
        ProcureAi.DemoData.Scenarios.Load(Path.Combine(AppContext.BaseDirectory, "Scenarios"));

    /// <summary>The seeded request of scenario <paramref name="key"/> in <paramref name="tenant"/>: its ID and number, found by its title.</summary>
    public async Task<(Guid Id, string Number)> SeededRequestAsync(string key, string tenant = TenantId)
    {
        var title = Scenarios.Single(s => s.Tenant == tenant).Requests.Single(r => r.Key == key).Title;
        // Newest first: on a deployment that keeps its data (external target) earlier runs' requests come before the seeded ones.
        for (var page = 1; ; page++)
        {
            var (body, _) = await CallAsync(HttpMethod.Get, $"/api/v1/requests?pageSize=100&page={page}", null, tenant: tenant);
            var items = body.GetProperty("items").EnumerateArray().ToList();
            var found = items.Where(i => i.GetProperty("title").GetString() == title).ToList();
            if (found.Count > 0)
            {
                var item = found.Single();
                return (item.GetProperty("id").GetGuid(), item.GetProperty("number").GetString()!);
            }
            if (items.Count < 100)
            {
                throw new InvalidOperationException($"The seeded request '{title}' ({key}, {tenant}) was not found.");
            }
        }
    }

    /// <summary>
    /// Runs SQL in the system's Postgres container as the superuser, the way someone with database access changes data behind the app's
    /// back (<c>docker exec ... psql</c>; in external target mode <c>kubectl exec</c> into the cluster's postgres-0). Returns psql's output;
    /// fails on a non-zero exit code.
    /// </summary>
    public async Task<string> PsqlAsync(string database, string sql)
    {
        System.Diagnostics.ProcessStartInfo start;
        if (IsExternal)
        {
            // The cluster's Postgres pod (procure/postgres-0): kubectl exec instead of docker exec. kubectl exec has no -e, so the SQL is
            // the shell's positional parameter $1 (still no shell quoting).
            var context = Environment.GetEnvironmentVariable(KubeContextVariable) is { Length: > 0 } c ? c : "k3d-procure";
            start = new System.Diagnostics.ProcessStartInfo("kubectl");
            foreach (var argument in new[]
                     {
                         "--context", context, "-n", "procure", "exec", "postgres-0", "-c", "postgres", "--", "sh", "-c",
                         $"PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -v ON_ERROR_STOP=1 -U postgres -d {database} -At -c \"$1\"", "sh", sql,
                     })
            {
                start.ArgumentList.Add(argument);
            }
        }
        else
        {
            if (!_app!.ResourceNotifications.TryGetCurrentState("postgres", out var state)
                || state.Snapshot.Properties.FirstOrDefault(p => p.Name == "container.id")?.Value is not string containerId)
            {
                throw new InvalidOperationException("The postgres container is not known yet.");
            }
            start = new System.Diagnostics.ProcessStartInfo("docker");
            // The SQL goes in an environment variable (no shell quoting); the password is the container's own POSTGRES_PASSWORD.
            foreach (var argument in new[]
                     {
                         "exec", "-e", $"PSQL_SQL={sql}", containerId, "sh", "-c",
                         $"PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -v ON_ERROR_STOP=1 -U postgres -d {database} -At -c \"$PSQL_SQL\"",
                     })
            {
                start.ArgumentList.Add(argument);
            }
        }
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"psql failed ({process.ExitCode}): {await error}");
        }
        return (await output).Trim();
    }

    /// <summary>The running AppHost; not there in external target mode (such tests call <see cref="RequireAppHost"/> first).</summary>
    private DistributedApplication AppHost =>
        _app ?? throw new InvalidOperationException("This test needs the AppHost, but the tests run against an external target.");

    /// <summary>Stops a resource the way the dashboard's Stop command does, and waits until it has ended.</summary>
    public async Task StopAsync(string resource)
    {
        var result = await AppHost.ResourceCommands.ExecuteCommandAsync(resource, KnownResourceCommands.StopCommand);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Stopping {resource} failed: {result.Message}");
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await AppHost.ResourceNotifications.WaitForResourceAsync(resource, KnownResourceStates.TerminalStates, cts.Token);
    }

    /// <summary>Starts a stopped resource (the dashboard's Start command) and waits until it is running and healthy.</summary>
    public async Task StartAsync(string resource)
    {
        var result = await AppHost.ResourceCommands.ExecuteCommandAsync(resource, KnownResourceCommands.StartCommand);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Starting {resource} failed: {result.Message}");
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await AppHost.ResourceNotifications.WaitForResourceHealthyAsync(resource, cts.Token);
    }

    /// <summary>The requests <see cref="SubmitRequestAsync"/> raised in this run.</summary>
    private readonly System.Collections.Concurrent.ConcurrentBag<Guid> _submitted = [];

    /// <summary>
    /// Against a deployment the data stays (plan 07 carry-over 24: the suite reruns on kept data, no demo reset needed): Tom cancels the
    /// requests of this run that are still waiting for an approval, so their IT-200 budget reservations do not pile up run after run
    /// (an approved one became an order; its spend is part of the demo). Best effort: a failure here never fails the run.
    /// </summary>
    private async Task CancelWaitingRequestsAsync()
    {
        foreach (var id in _submitted)
        {
            try
            {
                var (request, etag) = await CallAsync(HttpMethod.Get, $"/api/v1/requests/{id}", TomId);
                if (request.TryGetProperty("status", out var status) && status.ValueKind == System.Text.Json.JsonValueKind.String
                    && status.GetString() == "Submitted" && etag is not null)
                {
                    await CallAsync(HttpMethod.Post, $"/api/v1/requests/{id}/cancel", TomId, new { reason = "End-to-end run cleanup" }, etag);
                }
            }
            catch (Exception failure)
            {
                Console.WriteLine($"Cleanup: request {id} was not cancelled ({failure.GetType().Name}: {failure.Message})");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsExternal && !string.IsNullOrEmpty(_adminApiKey))
        {
            await CancelWaitingRequestsAsync();
        }

        if (Browser is not null)
        {
            await Browser.CloseAsync();
            Browser = null!;
        }
        _playwright?.Dispose();
        _playwright = null;
        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class EndToEndCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "end-to-end";
}

/// <summary>A browser context that writes its trace and final screenshots to <see cref="AppFixture.ArtifactsDirectory"/> when disposed.</summary>
public sealed class TracedContext(IBrowserContext context, string testName, string? videoDirectory = null) : IAsyncDisposable
{
    private long _pageStarted;

    public Task<IPage> NewPageAsync()
    {
        if (_pageStarted == 0)
        {
            _pageStarted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        return context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Directory.CreateDirectory(AppFixture.ArtifactsDirectory);
            var index = 0;
            foreach (var page in context.Pages)
            {
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(AppFixture.ArtifactsDirectory, $"{testName}-page{++index}.png"),
                });
            }
            await context.Tracing.StopAsync(new TracingStopOptions { Path = Path.Combine(AppFixture.ArtifactsDirectory, $"{testName}.zip") });
        }
        catch (Exception)
        {
            // Diagnostics must never turn a passing test into a failing one (or hide the real failure): not only
            // Playwright errors, also a full disk, a locked file or a path the file system rejects.
        }
        finally
        {
            // Always release the browser context, whatever the diagnostics did.
            var videos = videoDirectory is null ? [] : context.Pages.Select(p => p.Video).OfType<IVideo>().ToList();
            var ended = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await context.CloseAsync();
            await SaveVideosAsync(videos, ended);
        }
    }

    /// <summary>Saves the opt-in page videos (<see cref="AppFixture.VideoDirectoryVariable"/>); never fails the test.</summary>
    private async Task SaveVideosAsync(List<IVideo> videos, long ended)
    {
        for (var i = 0; i < videos.Count; i++)
        {
            try
            {
                var suffix = i == 0 ? "" : $"-page{i + 1}";
                await videos[i].SaveAsAsync(Path.Combine(videoDirectory!, $"{testName}{suffix}.{_pageStarted}-{ended}.webm"));
                await videos[i].DeleteAsync();
            }
            catch (Exception)
            {
                // A video is a nice-to-have, like the trace.
            }
        }
    }
}
