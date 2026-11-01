using Microsoft.Playwright;

namespace ProcureAi.EndToEndTests;

/// <summary>A decision made in one tab while another tab still shows the old inbox (plan 04: 412 / 409 on approvals opens the conflict dialog).</summary>
[Collection(EndToEndCollection.Name)]
public sealed class ConflictTests(AppFixture fixture)
{
    [Fact]
    public async Task Second_approval_tab_gets_conflict_dialog()
    {
        fixture.RequireReady();
        var (_, number) = await fixture.SubmitRequestAsync($"E2E conflict {Guid.NewGuid():N}"[..28]);

        // One browser, one session, two tabs on Maria's inbox.
        await using var context = await fixture.NewContextAsync();
        var tabA = await context.NewPageAsync();
        await Pages.LogInAsync(fixture, tabA, "maria@acme.test", "/app/approvals");
        await Assertions.Expect(Pages.InboxRow(tabA, number)).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });

        // Tab B cannot reach the hub, so nothing refreshes it: it keeps showing the step that tab A is about to decide.
        var tabB = await context.NewPageAsync();
        await tabB.RouteAsync("**/hubs/**", route => route.AbortAsync());
        await tabB.GotoAsync(new Uri(fixture.GatewayUrl, "/app/approvals").ToString());
        var rowB = Pages.InboxRow(tabB, number);
        await Assertions.Expect(rowB).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(Pages.LiveIndicator(tabB)).Not.ToHaveTextAsync("Live");

        // Tab A approves; the row leaves its list.
        await Pages.ApproveInInboxAsync(tabA, number);

        // Tab B approves the same step: the server refuses, the dialog says why, and "Show the latest" reloads the inbox without the row.
        await rowB.Locator("[data-action=approve] button").ClickAsync();
        await tabB.GetByRole(AriaRole.Dialog, new() { Name = $"Approve {number}?" }).GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
        var dialog = tabB.GetByRole(AriaRole.Dialog, new() { Name = "This was changed by someone else" });
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(dialog).ToContainTextAsync(number);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Show the latest" }).ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
        await Assertions.Expect(Pages.InboxRow(tabB, number)).ToHaveCountAsync(0, new() { Timeout = Pages.Timeout });
    }
}
