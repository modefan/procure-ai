using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// The whole procurement story in the browser, through the UI only (spec 8): Tom raises and submits a request, Maria approves it in her
/// inbox, the order goes to the (simulated) supplier and is confirmed, Tom records the delivery, the supplier's invoice matches, Thandi
/// releases the payment and the request ends Completed. Its Data changes tab then shows the edits behind it. Each person signs in with
/// their demo button in their own browser.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class FullFlowTests(AppFixture fixture)
{
    /// <summary>The supplier confirms, invoices and the ERP exports in seconds, but the steps hop over RabbitMQ: allow for a busy PC.</summary>
    private static readonly float Slow = (float)TimeSpan.FromMinutes(3).TotalMilliseconds;

    [Fact]
    public async Task Tom_requests_maria_approves_order_is_received_invoiced_and_paid()
    {
        fixture.RequireReady();
        var title = $"E2E full flow {Guid.NewGuid():N}"[..28];

        // Tom raises the request: one monitor from Contoso on IT-200, R 4 830,00 incl. VAT, so Maria alone approves it.
        await using var tomContext = await fixture.NewContextAsync("tom");
        var tom = await tomContext.NewPageAsync();
        await fixture.DemoLogInAsync(tom, "tom", "/app/requests/new");
        await tom.Locator("#title").FillAsync(title);
        await tom.Locator("#supplier").SelectOptionAsync("contoso-hardware");
        await tom.Locator("#cost-center").SelectOptionAsync("IT-200");
        await tom.GetByLabel("Item, line 1").SelectOptionAsync("MON-27");
        await Assertions.Expect(tom.Locator("#title")).ToHaveValueAsync(title);
        await tom.GetByTestId("submit").ClickAsync();
        await tom.WaitForURLAsync(new Regex(@"/app/requests/[0-9a-f-]{36}$"), new() { Timeout = Pages.Timeout });
        var requestUrl = tom.Url;
        var number = (await tom.Locator("main p.font-semibold").First.InnerTextAsync()).Split('·')[0].Trim();
        number.ShouldStartWith("PR-");
        await Assertions.Expect(Pages.RequestStatus(tom)).ToHaveTextAsync("Submitted", new() { Timeout = Pages.Timeout });

        // Maria approves it in her inbox.
        await using var mariaContext = await fixture.NewContextAsync("maria");
        var maria = await mariaContext.NewPageAsync();
        await fixture.DemoLogInAsync(maria, "maria", "/app/approvals");
        await Pages.ApproveInInboxAsync(maria, number);

        // Tom's request becomes an order, which the supplier confirms.
        await Assertions.Expect(Pages.RequestStatus(tom)).ToHaveTextAsync("Ordered", new() { Timeout = Slow });
        var order = tom.GetByRole(AriaRole.Region, new() { Name = "Order and delivery" });
        await Assertions.Expect(order).ToContainTextAsync("Confirmed", new() { Timeout = Slow });
        var orderNumber = Regex.Match(await order.InnerTextAsync(), @"PO-\d+").Value;
        orderNumber.ShouldNotBeEmpty();

        // Tom records the delivery: the order in his list, Record delivery, everything outstanding.
        await tom.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).GetByRole(AriaRole.Link, new() { Name = "Orders" }).ClickAsync();
        await tom.GetByRole(AriaRole.Link, new() { Name = orderNumber, Exact = true }).First.ClickAsync(new() { Timeout = Pages.Timeout });
        await tom.WaitForURLAsync(new Regex(@"/app/orders/[0-9a-f-]{36}$"), new() { Timeout = Pages.Timeout });
        var receipt = tom.Locator("[data-section=receipt-form]");
        await receipt.GetByLabel(new Regex("^Delivered now, ")).First.FillAsync("1", new() { Timeout = Pages.Timeout });
        await receipt.GetByRole(AriaRole.Button, new() { Name = "Record delivery" }).ClickAsync();
        await Assertions.Expect(tom.Locator("[data-section=receipts]")).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });

        // The supplier's invoice arrives by webhook and matches the order and the delivery; Thandi pays it.
        await using var thandiContext = await fixture.NewContextAsync("thandi");
        var thandi = await thandiContext.NewPageAsync();
        await fixture.DemoLogInAsync(thandi, "thandi", "/app/invoices");
        var invoiceRow = thandi.GetByRole(AriaRole.Row).Filter(new() { HasText = orderNumber });
        await Assertions.Expect(invoiceRow).ToContainTextAsync("Matched", new() { Timeout = Slow });
        await invoiceRow.GetByRole(AriaRole.Link).First.ClickAsync();
        await thandi.WaitForURLAsync(new Regex(@"/app/invoices/[0-9a-f-]{36}$"), new() { Timeout = Pages.Timeout });
        await thandi.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^Pay invoice \(R") }).ClickAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(thandi.Locator("[data-section=payments]")).ToContainTextAsync("released by you", new() { Timeout = Pages.Timeout });

        // The payment is exported to the ERP and the request is done.
        await tom.GotoAsync(requestUrl);
        await Assertions.Expect(Pages.RequestStatus(tom)).ToHaveTextAsync("Completed", new() { Timeout = Slow });

        // The Data changes tab shows the row edits behind the story, made in the app by the people who acted. The tab reads what Audit
        // has when it is opened; the last edits (Completed) reach Audit through Debezium a moment after the push, so open it again until
        // they are there.
        var changes = tom.GetByTestId("data-changes");
        for (var attempt = 0; ; attempt++)
        {
            await tom.GetByRole(AriaRole.Tab, new() { Name = "Data changes" }).ClickAsync();
            await Assertions.Expect(changes).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
            // Oldest first, so the last edits are on the last page.
            var next = tom.GetByRole(AriaRole.Button, new() { Name = "Next page" });
            while (await next.IsVisibleAsync() && await next.IsEnabledAsync())
            {
                var before = await changes.InnerTextAsync();
                await next.ClickAsync();
                await Assertions.Expect(changes).Not.ToHaveTextAsync(before, new() { Timeout = Pages.Timeout });
            }
            if ((await changes.InnerTextAsync()).Contains("Completed", StringComparison.Ordinal) || attempt == 20)
            {
                break;
            }
            await tom.WaitForTimeoutAsync(3000);
            await tom.ReloadAsync();
            await Assertions.Expect(Pages.RequestStatus(tom)).ToHaveTextAsync("Completed", new() { Timeout = Pages.Timeout });
        }
        await Assertions.Expect(changes).ToContainTextAsync("purchase_requests");
        await Assertions.Expect(changes).ToContainTextAsync("Completed");
        await Assertions.Expect(changes).ToContainTextAsync("Thandi");
        await Assertions.Expect(changes).Not.ToContainTextAsync("outside the app");
    }
}
