using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Playwright;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// One AppHost (the whole system) and one headless Chromium for the whole run. Secrets are read from the same places
/// the AppHost uses (its user secrets, and the user environment variable DEMO_ADMIN_API_KEY) and never printed.
/// If they are missing the tests skip with a message instead of failing: CI wiring comes with the pipeline task.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public const string TenantId = "acme";
    public const string DemoUser = "tom@acme.test";

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

    public async ValueTask InitializeAsync()
    {
        _adminApiKey = Environment.GetEnvironmentVariable("DEMO_ADMIN_API_KEY")
                       ?? Environment.GetEnvironmentVariable("DEMO_ADMIN_API_KEY", EnvironmentVariableTarget.User);

        // Tests never keep data: no volumes, so every run starts from empty databases.
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ProcureAi_AppHost>(
            ["Persistence:UseDataVolumes=false"]);
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
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        // xUnit does not reliably call DisposeAsync after a failed InitializeAsync, so clean up here and rethrow.
        try
        {
            _app = await builder.BuildAsync();
            await _app.StartAsync();

            using var cts = new CancellationTokenSource(StartupTimeout);
            await _app.ResourceNotifications.WaitForResourceHealthyAsync("gateway", cts.Token);
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

    /// <summary>
    /// A fresh, isolated browser context (own cookies) that records a Playwright trace. On dispose it saves the trace
    /// (<c>&lt;test&gt;.zip</c>) and a screenshot of every open page (<c>&lt;test&gt;-pageN.png</c>) into <see cref="ArtifactsDirectory"/>.
    /// A test that fails inside <c>await using</c> disposes while unwinding, so the screenshot shows the failing state.
    /// The gateway is https with the ASP.NET dev certificate.
    /// </summary>
    public async Task<TracedContext> NewContextAsync([System.Runtime.CompilerServices.CallerMemberName] string testName = "test")
    {
        var context = await Browser.NewContextAsync(
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = 1280, Height = 800 } });
        await context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true, Sources = false });
        return new TracedContext(context, testName);
    }

    /// <summary>Creates a purchase request through the gateway with the admin API key; returns its number.</summary>
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
                costCenterCode = "CC-100",
                supplierId = "sup-1",
                lines = new[] { new { sku = "LAP-1", description = "Laptop", quantity = 3, unitPriceExclVat = 25200m } },
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

    public async ValueTask DisposeAsync()
    {
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
public sealed class TracedContext(IBrowserContext context, string testName) : IAsyncDisposable
{
    public Task<IPage> NewPageAsync() => context.NewPageAsync();

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
        catch (PlaywrightException)
        {
            // Diagnostics must never turn a passing test into a failing one (or hide the real failure).
        }
        await context.CloseAsync();
    }
}
