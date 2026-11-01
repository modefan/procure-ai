using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// What each demo role sees in the header (spec 8): the links come from the user's permissions (the default matrix of spec 4.2), never
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

        var nav = page.GetByRole(AriaRole.Navigation, new() { Name = "Main" });
        await Assertions.Expect(nav.GetByRole(AriaRole.Link, new() { Name = "Dashboard" })).ToBeVisibleAsync();
        // The links appear with the permissions; wait for the last one before reading them all.
        await Assertions.Expect(nav.GetByRole(AriaRole.Link)).ToHaveCountAsync(links.Length, new() { Timeout = Pages.Timeout });
        var shown = (await nav.GetByRole(AriaRole.Link).AllInnerTextsAsync())
            .Select(text => Regex.Replace(text, @"\s*\d+\s*$", "").Trim()) // the Approvals link carries its count
            .ToArray();
        shown.ShouldBe(links, $"{role}'s menu");

        var admin = page.GetByTestId("admin-menu");
        if (adminLinks.Length == 0)
        {
            await Assertions.Expect(admin).ToHaveCountAsync(0);
            return;
        }
        await admin.ClickAsync();
        var menu = page.GetByTestId("admin-links");
        await Assertions.Expect(menu.GetByRole(AriaRole.Link)).ToHaveCountAsync(adminLinks.Length);
        (await menu.GetByRole(AriaRole.Link).AllInnerTextsAsync()).Select(t => t.Trim()).ToArray().ShouldBe(adminLinks, $"{role}'s admin menu");
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
