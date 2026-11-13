using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;

namespace ProcureAi.EndToEndTests;

/// <summary>Smoke tests through the real gateway, Keycloak and Angular app, in a headless Chromium.</summary>
[Collection(EndToEndCollection.Name)]
public sealed class WelcomeAndLoginTests(AppFixture fixture)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    // Text that only exists in the lazy requests page template (the public welcome page also says "Purchase requests,",
    // so a shorter marker matched the public bundle), used to recognise the lazy `app` chunk.
    private const string AppChunkMarker = "No purchase requests yet";

    [Fact]
    public async Task Procurement_demo_page_shows_login_but_no_register_when_logged_out()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        // The Welcome page: who Dale is and a card per demo, which leads to the Procurement demo page.
        await page.GotoAsync(fixture.GatewayUrl.ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Hi, I'm Dale Abels" })).ToBeVisibleAsync();
        await page.GetByTestId("demo-card").GetByRole(AriaRole.Link, new() { Name = "Try it" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Welcome to ProcureAI" })).ToBeVisibleAsync();
        var main = page.Locator("main");
        await Assertions.Expect(main.GetByRole(AriaRole.Link, new() { Name = "Log in" })).ToBeVisibleAsync();
        // Self-registration is off (plan 08 Task 12c: the realm's registrationAllowed and the app's registration.enabled).
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Register" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("feature-tile")).ToHaveCountAsync(4);
    }

    [Fact]
    public async Task Anonymous_deep_link_to_app_requests_goes_to_keycloak_login()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        var log = new NetworkLog(page);

        await page.GotoAsync(new Uri(fixture.GatewayUrl, "/app/requests").ToString());
        await page.WaitForURLAsync(KeycloakLogin(), new() { Timeout = (float)Wait.TotalMilliseconds });
        await Assertions.Expect(page.Locator("#username")).ToBeVisibleAsync();
        // Keycloak offers no sign-up (registrationAllowed false, plan 08 Task 12c): no "Register" link on its login page.
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Register" })).ToHaveCountAsync(0);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // The main evidence is the redirect itself: authGuard is a canMatch guard, so for a visitor the `app` route never
        // matches and its chunk is never requested. The network log below backs that up but cannot prove it alone (waiting
        // for NetworkIdle on the Keycloak page does not bound when Angular would have started a lazy load). How the log
        // is used: the anonymous visit
        // downloaded some JavaScript (the public bundle: this proves the log works), none of that JavaScript contains
        // the text of the requests page, and no call to the requests API was made. The marker is validated the other
        // way round in Tom_logs_in_and_sees_requests_list, where the same text must appear in a downloaded chunk.
        var scripts = await log.ScriptsAsync();
        scripts.ShouldNotBeEmpty("no JavaScript was captured, so the check would pass on nothing");
        // An unreadable body is only tolerated for the dev server's component hot-reload probes (/@ng/component?c=<file>@<Class>),
        // which the browser aborts when the page navigates away to Keycloak; even those must not name an app feature.
        scripts.Where(s => s.Body is null && !IsPublicComponentProbe(s.Url)).Select(s => s.Url)
            .ShouldBeEmpty("a script body could not be read, so it could not be checked for the app chunk");
        scripts.Where(s => s.Body is not null && s.Body.Contains(AppChunkMarker, StringComparison.Ordinal)).Select(s => s.Url)
            .ShouldBeEmpty("the lazy app chunk was downloaded by an anonymous visitor");
        log.Urls.Where(u => u.Contains("/api/v1/requests", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deep_link_signed_out_keeps_return_url()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        // The public menu, signed out: Welcome and the demo, none of the signed-in pages.
        await page.GotoAsync(new Uri(fixture.GatewayUrl, "/").ToString());
        var nav = page.GetByRole(AriaRole.Navigation, new() { Name = "Main" });
        await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { Name = "Welcome", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { Name = "ProcureAI", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { Name = "Requests", Exact = true })).ToHaveCountAsync(0);

        // Signed out, the deep link goes to Keycloak and, after the sign-in, back to the same address (the BFF's return url).
        await LogInAsync(page, "/app/requests");

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Purchase requests" })).ToBeVisibleAsync();
        await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { Name = "Requests", Exact = true })).ToHaveAttributeAsync("aria-current", "page");
        await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { Name = "Welcome", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Tom_logs_in_and_sees_requests_list()
    {
        fixture.RequireReady();
        var title = $"E2E laptops {Guid.NewGuid():N}"[..28];
        var number = await fixture.SeedRequestAsync(title);

        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        var log = new NetworkLog(page);

        await LogInAsync(page, "/app/requests");

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Purchase requests" })).ToBeVisibleAsync();
        var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = title });
        await Assertions.Expect(row).ToBeVisibleAsync();
        await Assertions.Expect(row).ToContainTextAsync(number);
        // 3 x R 25 200,00 = R 75 600,00 excl. VAT, R 86 940,00 incl. 15% VAT, formatted for en-ZA (R, space as
        // thousands separator, decimal comma). \s also matches the non-breaking spaces .NET/ICU may use.
        await Assertions.Expect(row).ToContainTextAsync(new Regex(@"R\s?86\s?940,00"));

        // Positive control for the lazy-chunk check above: once signed in, the marker text is in a downloaded chunk.
        (await log.ScriptsAsync()).ShouldContain(s => s.Body != null && s.Body.Contains(AppChunkMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logged_in_user_visiting_login_goes_to_dashboard()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await LogInAsync(page, "/app/dashboard");

        await page.GotoAsync(new Uri(fixture.GatewayUrl, "/login").ToString());

        await page.WaitForURLAsync(new Regex(@"/app/dashboard$"), new() { Timeout = (float)Wait.TotalMilliseconds });
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" })).ToBeVisibleAsync();
    }

    private static bool IsPublicComponentProbe(string url) =>
        new Uri(url).AbsolutePath == "/@ng/component"
        && !url.Contains("features%2Frequests", StringComparison.OrdinalIgnoreCase)
        && !url.Contains("features%2Fdashboard", StringComparison.OrdinalIgnoreCase);

    private Regex KeycloakLogin() =>
        new("^" + Regex.Escape(fixture.KeycloakUrl.GetLeftPart(UriPartial.Authority)) + "/realms/procure/");

    /// <summary>Opens a protected page, signs in as Tom at Keycloak and waits to land back on it.</summary>
    private async Task LogInAsync(IPage page, string path)
    {
        await page.GotoAsync(new Uri(fixture.GatewayUrl, path).ToString());
        await page.WaitForURLAsync(KeycloakLogin(), new() { Timeout = (float)Wait.TotalMilliseconds });

        await page.Locator("#username").FillAsync(AppFixture.DemoUser);
        var password = page.Locator("#password");
        if (!await password.IsVisibleAsync())
        {
            // This realm asks for the username first and the password on a second page.
            await page.Locator("#kc-login").ClickAsync();
            await password.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        }
        await password.FillAsync(fixture.DemoPassword);
        await page.Locator("#kc-login").ClickAsync();

        await page.WaitForURLAsync(new Uri(fixture.GatewayUrl, path).ToString(), new() { Timeout = (float)Wait.TotalMilliseconds });
    }

    /// <summary>Every request the page makes, and the body of every JavaScript response, from the moment it is created.</summary>
    private sealed class NetworkLog
    {
        private readonly ConcurrentQueue<string> _urls = new();
        private readonly ConcurrentQueue<Task<(string Url, string? Body)>> _scripts = new();

        private readonly IAPIRequestContext _request;

        public NetworkLog(IPage page)
        {
            _request = page.Context.APIRequest;
            page.Request += (_, request) => _urls.Enqueue(request.Url);
            page.Response += (_, response) =>
            {
                if (response.Request.ResourceType == "script")
                {
                    _scripts.Enqueue(ReadAsync(response));
                }
            };
        }

        public IReadOnlyList<string> Urls => [.. _urls];

        public async Task<IReadOnlyList<(string Url, string? Body)>> ScriptsAsync() =>
            [.. await Task.WhenAll(_scripts.ToArray())];

        private async Task<(string Url, string? Body)> ReadAsync(IResponse response)
        {
            try
            {
                return (response.Url, await response.TextAsync());
            }
            catch (PlaywrightException)
            {
                // The browser drops a response body when the page navigates away (here: the redirect to Keycloak) before it was read;
                // seen on CI for two public chunks. A built chunk's URL is content-hashed, so fetching the same URL again (same
                // context: cookies, certificate settings) returns the same JavaScript and the check still reads it.
                return (response.Url, await RefetchAsync(response.Url));
            }
        }

        private async Task<string?> RefetchAsync(string url)
        {
            try
            {
                var again = await _request.GetAsync(url);
                return again.Ok ? await again.TextAsync() : null;
            }
            catch (PlaywrightException)
            {
                return null; // reported by the caller: an unreadable script must fail the negative check
            }
        }
    }
}
