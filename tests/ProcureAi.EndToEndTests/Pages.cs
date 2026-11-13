using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ProcureAi.EndToEndTests;

/// <summary>What the flow tests share: signing in as a demo user at Keycloak, the shell's locators and the wait for the live connection.</summary>
internal static class Pages
{
    public static readonly float Timeout = (float)TimeSpan.FromSeconds(60).TotalMilliseconds;

    public static Regex KeycloakLogin(AppFixture fixture) =>
        new("^" + Regex.Escape(fixture.KeycloakUrl.GetLeftPart(UriPartial.Authority)) + "/realms/procure/");

    /// <summary>Opens a protected page, signs in at Keycloak as <paramref name="email"/> and waits to land back on it.</summary>
    public static async Task LogInAsync(AppFixture fixture, IPage page, string email, string path)
    {
        await page.GotoAsync(new Uri(fixture.GatewayUrl, path).ToString());
        await SignInAtKeycloakAsync(fixture, page, email);
        await page.WaitForURLAsync(new Uri(fixture.GatewayUrl, path).ToString(), new() { Timeout = Timeout });
    }

    /// <summary>On the Keycloak login page: username, (password on a second page when asked), submit.</summary>
    public static async Task SignInAtKeycloakAsync(AppFixture fixture, IPage page, string email)
    {
        await page.WaitForURLAsync(KeycloakLogin(fixture), new() { Timeout = Timeout });
        await page.Locator("#username").FillAsync(email);
        var password = page.Locator("#password");
        if (!await password.IsVisibleAsync())
        {
            await page.Locator("#kc-login").ClickAsync();
            await password.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        }
        await password.FillAsync(fixture.DemoPassword);
        await page.Locator("#kc-login").ClickAsync();
    }

    /// <summary>
    /// The button labels of the welcome page's "Try a demo role" section, by demo login key (the gateway's <c>DemoLogin:Users</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DemoRoles = new Dictionary<string, string>
    {
        ["tom"] = "Requester (Tom)",
        ["maria"] = "Manager (Maria)",
        ["dept"] = "Department head",
        ["thandi"] = "Finance (Thandi)",
        ["admin"] = "Admin",
        ["ben"] = "Globex manager (Ben)",
    };

    /// <summary>
    /// Signs in the way a reviewer does: the Procurement demo page's demo button for <paramref name="key"/> (no Keycloak page, so Thandi, who is in
    /// two companies, gets no organization picker), then waits for the dashboard and, when <paramref name="path"/> is given, opens it.
    /// </summary>
    public static async Task DemoLogInAsync(AppFixture fixture, IPage page, string key, string? path = null)
    {
        // The gateway allows 10 demo logins a minute per client IP (RateLimiting:DemoEntry) and a whole run signs in more often than that:
        // when the demo page says the sign-in failed, wait for the next window and click again (twice at most).
        for (var attempt = 1; ; attempt++)
        {
            await page.GotoAsync(new Uri(fixture.GatewayUrl, "demos/procurement").ToString());
            var section = page.GetByTestId("demo-login");
            await section.GetByRole(AriaRole.Button).Filter(new() { Has = page.GetByTestId("demo-label").GetByText(DemoRoles[key], new() { Exact = true }) })
                .ClickAsync(new() { Timeout = Timeout });
            var refused = section.GetByText("could not sign you in");
            await page.GetByTestId("user-menu").Or(refused).First.WaitForAsync(new() { Timeout = Timeout });
            if (!await refused.IsVisibleAsync())
            {
                break;
            }
            if (attempt == 3)
            {
                throw new InvalidOperationException($"The demo login as {key} was refused three times.");
            }
            await page.WaitForTimeoutAsync(30_000);
        }
        await page.WaitForURLAsync(new Regex(@"/app/dashboard$"), new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("user-menu")).ToBeVisibleAsync(new() { Timeout = Timeout });
        if (path is not null)
        {
            await page.GotoAsync(new Uri(fixture.GatewayUrl, path).ToString());
        }
    }

    /// <summary>The header's connection indicator (Live / Reconnecting… / Offline).</summary>
    public static ILocator LiveIndicator(IPage page) => page.GetByTestId("live-indicator");

    /// <summary>Waits until the page's live connection is up. Pushes only reach a page that is connected.</summary>
    public static Task ExpectLiveAsync(IPage page, float? timeout = null) =>
        Assertions.Expect(LiveIndicator(page)).ToHaveTextAsync("Live", new() { Timeout = timeout ?? Timeout });

    /// <summary>A request's row in the inbox table (the wide layout; the tests use a 1280 px window).</summary>
    public static ILocator InboxRow(IPage page, string number) => page.Locator("tr[data-step]").Filter(new() { HasText = number });

    /// <summary>
    /// Approves the request's row in the inbox: the row's Approve button, then Approve in the confirm dialog (which asks for an optional
    /// comment since plan 06 Task 0b), and waits until the row has left the inbox.
    /// </summary>
    public static async Task ApproveInInboxAsync(IPage page, string number)
    {
        var row = InboxRow(page, number);
        await Assertions.Expect(row).ToBeVisibleAsync(new() { Timeout = Timeout });
        await row.Locator("[data-action=approve] button").ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = $"Approve {number}?" });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
        await Assertions.Expect(row).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    /// <summary>The request's status pill in the detail page's header.</summary>
    public static ILocator RequestStatus(IPage page) => page.Locator("main app-status-badge").First;
}
