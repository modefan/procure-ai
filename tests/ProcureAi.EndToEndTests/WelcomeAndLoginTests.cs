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
    public async Task Welcome_shows_login_and_register_when_logged_out()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(fixture.GatewayUrl.ToString());

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Welcome to ProcureAI" })).ToBeVisibleAsync();
        var main = page.Locator("main");
        await Assertions.Expect(main.GetByRole(AriaRole.Link, new() { Name = "Log in" })).ToBeVisibleAsync();
        await Assertions.Expect(main.GetByRole(AriaRole.Link, new() { Name = "Register" })).ToBeVisibleAsync();
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
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // The app chunk is lazy behind canMatch(authGuard). How we know it was not requested: the anonymous visit
        // downloaded some JavaScript (the public bundle: this proves the log works), none of that JavaScript contains
        // the text of the requests page, and no call to the requests API was made. The marker is validated the other
        // way round in Tom_logs_in_and_sees_requests_list, where the same text must appear in a downloaded chunk.
        var scripts = await log.ScriptsAsync();
        scripts.ShouldNotBeEmpty("no JavaScript was captured, so the check would pass on nothing");
        scripts.Where(s => s.Body.Contains(AppChunkMarker, StringComparison.Ordinal)).Select(s => s.Url)
            .ShouldBeEmpty("the lazy app chunk was downloaded by an anonymous visitor");
        log.Urls.Where(u => u.Contains("/api/v1/requests", StringComparison.Ordinal)).ShouldBeEmpty();
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
        // 3 x R 25 200,00 excl. VAT: the amount is formatted for en-ZA (R, space as thousands separator, decimal comma).
        await Assertions.Expect(row).ToContainTextAsync(new Regex(@"R\s?\d[\d\s ]*,\d\d"));

        // Positive control for the lazy-chunk check above: once signed in, the marker text is in a downloaded chunk.
        (await log.ScriptsAsync()).ShouldContain(s => s.Body.Contains(AppChunkMarker, StringComparison.Ordinal));
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
        private readonly ConcurrentQueue<Task<(string Url, string Body)?>> _scripts = new();

        public NetworkLog(IPage page)
        {
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

        public async Task<IReadOnlyList<(string Url, string Body)>> ScriptsAsync() =>
            [.. (await Task.WhenAll(_scripts.ToArray())).Where(s => s is not null).Select(s => s!.Value)];

        private static async Task<(string Url, string Body)?> ReadAsync(IResponse response)
        {
            try
            {
                return (response.Url, await response.TextAsync());
            }
            catch (PlaywrightException)
            {
                return null; // body no longer available (navigated away); not a script we can judge
            }
        }
    }
}
