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

    /// <summary>The header's connection indicator (Live / Reconnecting… / Offline).</summary>
    public static ILocator LiveIndicator(IPage page) => page.GetByTestId("live-indicator");

    /// <summary>Waits until the page's live connection is up. Pushes only reach a page that is connected.</summary>
    public static Task ExpectLiveAsync(IPage page, float? timeout = null) =>
        Assertions.Expect(LiveIndicator(page)).ToHaveTextAsync("Live", new() { Timeout = timeout ?? Timeout });

    /// <summary>A request's row in the inbox table (the wide layout; the tests use a 1280 px window).</summary>
    public static ILocator InboxRow(IPage page, string number) => page.Locator("tr[data-step]").Filter(new() { HasText = number });

    /// <summary>The request's status pill in the detail page's header.</summary>
    public static ILocator RequestStatus(IPage page) => page.Locator("main app-status-badge").First;
}
