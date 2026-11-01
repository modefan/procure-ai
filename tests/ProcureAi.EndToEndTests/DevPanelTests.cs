using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// "Behind the scenes" (wireframe 19, plan 06 Task 10) against the real gateway and services: with the panel open the services answer
/// with their dev headers, so a repeated read within the 1-second micro-cache shows as a cache HIT (with its age), and every call links to its trace in
/// the Aspire dashboard.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class DevPanelTests(AppFixture fixture)
{
    [Fact]
    public async Task Dev_panel_shows_cache_hit_and_trace_link()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await fixture.DemoLogInAsync(page, "maria");

        // Open the panel from the user menu (Alt+Shift+D does the same).
        await page.GetByTestId("user-menu").ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Behind the scenes" }).ClickAsync();
        var panel = page.GetByTestId("dev-panel");
        await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });

        // The panel stays open across a full page load (sessionStorage) and records the calls of the app's start. At start the shell
        // and the dashboard both read Maria's inbox and her bell within a second: the second read (and every read of the
        // page load that came within a second of the first one) is served from the services' 1-second micro-cache: a HIT, with its age.
        await page.GotoAsync(new Uri(fixture.GatewayUrl, "/app/dashboard").ToString());
        await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        var calls = panel.GetByTestId("dev-call");
        var hit = calls.Filter(new() { HasTextRegex = new Regex(@"cache HIT \(\d+(\.\d)? s old\)") }).First;
        await Assertions.Expect(hit).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        var link = hit.GetByTestId("trace-link");
        await Assertions.Expect(link).ToHaveAttributeAsync("href", new Regex(@"^https?://[^/]+/traces/detail/[0-9a-f]{32}$"));
        await Assertions.Expect(link).ToContainTextAsync("open in Aspire");
    }
}
