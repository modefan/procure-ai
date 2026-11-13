using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// What each demo role sees in the left menu (spec 8, plan 09): the links come from the user's permissions (the default matrix of spec 4.2), never
/// from role names, and a page without the permission is the 403 page naming the permission it needs. Signed in with the demo buttons.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class RoleMenuTests(AppFixture fixture)
{
    private static readonly string[] Basics = ["Dashboard", "Requests", "Orders", "Invoices", "Budgets", "Assistant"];
    private static readonly string[] Approvers = ["Dashboard", "Requests", "Approvals", "Orders", "Invoices", "Budgets", "Assistant"];
    private static readonly string[] AdminPages = ["Authorization", "Access check", "Users", "Policies", "System status", "Conversations", "Data changes"];

    public static TheoryData<string, string[], string[]> Roles => new()
    {
        { "tom", Basics, [] },
        { "maria", Approvers, [] },
        { "dept", Approvers, [] },
        { "thandi", Approvers, [] },
        // The admin manages the system: no requests of their own to raise or approve, so no Approvals link.
        { "admin", Basics, AdminPages },
        { "ben", Approvers, [] },
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Each_role_sees_its_menu(string role, string[] links, string[] adminLinks)
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync(role);
        var page = await context.NewPageAsync();
        await fixture.DemoLogInAsync(page, role);

        // The left menu (plan 09): Welcome, the demo with "How it is built" and the pages this user may open (then the Admin group's
        // pages), and "Under the hood" last. The app pages are the links between "How it is built" and "API (Swagger)".
        var nav = page.GetByRole(AriaRole.Navigation, new() { Name = "Main" });
        var expected = links.Concat(adminLinks).ToArray();
        await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { Name = "API (Swagger)" })).ToBeVisibleAsync();
        // The links appear with the permissions; wait for the last one before reading them all.
        await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { Name = expected[^1], Exact = true })).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        var shown = (await nav.GetByRole(AriaRole.Link).AllInnerTextsAsync())
            .Select(text => Regex.Replace(text, @"\s*\d+\s*$", "").Trim()) // the Approvals link carries its count
            .ToList();
        shown.Take(3).ShouldBe(["Welcome", "ProcureAI", "How it is built"], $"{role}'s menu starts with the portfolio");
        var swagger = shown.IndexOf("API (Swagger)");
        shown.Skip(3).Take(swagger - 3).ToArray().ShouldBe(expected, $"{role}'s menu");

        // The Admin group heading is there only with admin pages under it.
        await Assertions.Expect(nav.GetByText("Admin", new() { Exact = true })).ToHaveCountAsync(adminLinks.Length == 0 ? 0 : 1);
        // The user menu sits in the top bar beside the menu at desktop widths.
        foreach (var width in new[] { 1440, 1280 })
        {
            await page.SetViewportSizeAsync(width, 800);
            await Assertions.Expect(nav).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("user-menu")).ToBeVisibleAsync();
        }
    }

    [Fact]
    public async Task Forbidden_routes_show_403_with_permission()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await fixture.DemoLogInAsync(page, "tom");

        // Typing the address of an admin page: the guard sends Tom to the 403 page, which names the permission it needs.
        await page.GotoAsync(new Uri(fixture.GatewayUrl, "/app/admin/authorization").ToString());
        await page.WaitForURLAsync(new Regex(@"/forbidden$"), new() { Timeout = Pages.Timeout });
        await Assertions.Expect(page.GetByText("403", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "You don't have access" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("main code")).ToHaveTextAsync("admin.authorization");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Authorization matrix" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Back to dashboard" })).ToBeVisibleAsync();
    }
}
