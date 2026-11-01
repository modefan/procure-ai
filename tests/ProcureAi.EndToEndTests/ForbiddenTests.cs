using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;

namespace ProcureAi.EndToEndTests;

/// <summary>What a signed-in user may not see: pages without the permission, and the data of the user who used this browser before.</summary>
[Collection(EndToEndCollection.Name)]
public sealed class ForbiddenTests(AppFixture fixture)
{
    [Fact]
    public async Task Tom_cannot_open_approvals_and_sees_forbidden()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await Pages.LogInAsync(fixture, page, AppFixture.DemoUser, "/app/dashboard");

        // A requester has no Approvals link, and typing the address gives the 403 page, not the inbox.
        var nav = page.GetByRole(AriaRole.Navigation, new() { Name = "Main" });
        await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { Name = "Requests" })).ToBeVisibleAsync();
        await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Approvals") })).ToHaveCountAsync(0);
        await page.GotoAsync(new Uri(fixture.GatewayUrl, "/app/approvals").ToString());
        await page.WaitForURLAsync(new Regex(@"/forbidden$"), new() { Timeout = Pages.Timeout });
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "You don't have access" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Approvals" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Back to dashboard" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Logout_then_login_as_Ben_shows_none_of_Toms_data()
    {
        fixture.RequireReady();
        var title = $"E2E private {Guid.NewGuid():N}"[..28];
        var (requestId, number) = await fixture.SubmitRequestAsync(title);

        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await Pages.LogInAsync(fixture, page, AppFixture.DemoUser, "/app/requests");
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = title })).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Pages.ExpectLiveAsync(page);

        // Tom logs out (the app stays loaded: no reload) ...
        await page.GetByTestId("user-menu").ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Log out", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Welcome to ProcureAI" })).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        (await page.ContentAsync()).ShouldNotContain(title);
        await Assertions.Expect(Pages.LiveIndicator(page)).ToHaveCountAsync(0);

        // ... and "Back" does not bring his list back from memory: the guard sends the browser to sign in.
        await page.GoBackAsync();
        await page.WaitForURLAsync(Pages.KeycloakLogin(fixture), new() { Timeout = Pages.Timeout });
        (await page.ContentAsync()).ShouldNotContain(title);

        // Ben (another company) signs in on the same browser.
        await Pages.SignInAtKeycloakAsync(fixture, page, "ben@globex.test");
        await page.WaitForURLAsync(new Regex(@"/app/"), new() { Timeout = Pages.Timeout });
        await page.GotoAsync(new Uri(fixture.GatewayUrl, "/app/requests").ToString());
        await Assertions.Expect(page.GetByTestId("user-menu")).ToContainTextAsync("Ben", new() { Timeout = Pages.Timeout });
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Purchase requests" })).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        // Globex's own (seeded) requests, never Acme's.
        var globex = AppFixture.Scenarios.Single(s => s.Tenant == "globex").Requests[0].Title;
        await Assertions.Expect(page.Locator("main table")).ToContainTextAsync(globex, new() { Timeout = Pages.Timeout });
        (await page.ContentAsync()).ShouldNotContain(title);
        (await page.ContentAsync()).ShouldNotContain(number);

        // The bell is Ben's own, and the browser cache holds nothing of Tom's: his request is neither served from cache nor from the server.
        var fromCache = await page.EvaluateAsync<string>(
            "async (id) => { try { const r = await fetch('/api/v1/requests/' + id, { cache: 'only-if-cached', mode: 'same-origin' }); return String(r.status); } catch { return 'miss'; } }",
            requestId.ToString());
        fromCache.ShouldBe("miss");
        var fromServer = await page.EvaluateAsync<int>(
            "async (id) => (await fetch('/api/v1/requests/' + id, { cache: 'no-store' })).status", requestId.ToString());
        fromServer.ShouldBeOneOf(403, 404);
        (await page.EvaluateAsync<string>("async () => await (await fetch('/api/v1/notifications', { cache: 'no-store' })).text()"))
            .ShouldNotContain(number);
    }
}
