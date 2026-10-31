using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// The AI screens in the browser (plan 05 Task 10), on the Fake provider ("Demo model"): Tom asks the assistant and watches the answer
/// stream with its sources, a rendered table and the tool it used; Stop keeps what was written; an admin uploads a policy document and
/// sees it go to Ready; Tom scans the invoice of his order and saves it from the prefilled form.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class AssistantTests(AppFixture fixture)
{
    /// <summary>An amount and a cost center: the Fake model calls explain_approval_chain, then quotes the 4.2 thresholds table.</summary>
    private const string ApproversQuestion = "Who approves R 250 000 for IT-200?";

    private static ILocator Question(IPage page) => page.GetByTestId("question");

    private static ILocator LastAnswer(IPage page) => page.GetByTestId("answer").Last;

    /// <summary>After a reload the page starts a new conversation; the newest one is first in the list.</summary>
    private static Task OpenLatestConversationAsync(IPage page) =>
        page.GetByTestId("conversations").Locator("li").First.Locator("button").First.ClickAsync(new() { Timeout = Pages.Timeout });

    private static async Task AskAsync(IPage page, string question)
    {
        await Question(page).FillAsync(question);
        await page.GetByTestId("send").ClickAsync();
    }

    [Fact]
    public async Task Tom_asks_and_sees_streamed_answer_with_sources_and_tool_step()
    {
        fixture.RequireReady();
        await fixture.WaitForSeededPolicyAsync();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await Pages.LogInAsync(fixture, page, AppFixture.DemoUser, "/app/assistant");
        await Assertions.Expect(page.GetByTestId("demo-model")).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });

        await AskAsync(page, ApproversQuestion);

        // While it streams: Stop instead of Send, and the answer marked busy.
        var answer = LastAnswer(page);
        await Assertions.Expect(page.GetByTestId("stop")).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(answer).ToHaveAttributeAsync("aria-busy", "true");

        // The tool step, the sources and the quoted thresholds as a real table.
        var step = answer.Locator("[data-tool='explain_approval_chain']");
        await Assertions.Expect(step).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(step).ToContainTextAsync("IT-200");
        await Assertions.Expect(answer.Locator(".md-answer table")).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(answer.Locator(".md-answer table")).ToContainTextAsync("Department head and finance, and the line manager when the total is within their approval limit");
        await Assertions.Expect(answer.GetByTestId("sources").Locator("li").First).ToBeVisibleAsync();
        await Assertions.Expect(answer.GetByTestId("sources")).ToContainTextAsync("4.2 Approval thresholds");

        // Done: Send is back, the answer is no longer busy, the tool finished well, and the answer is kept after a reload.
        await Assertions.Expect(page.GetByTestId("send")).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(answer).Not.ToHaveAttributeAsync("aria-busy", "true");
        await Assertions.Expect(step).ToHaveAttributeAsync("data-status", "ok");
        await Assertions.Expect(answer.GetByTestId("message-status")).ToHaveCountAsync(0);
        await page.ReloadAsync();
        await OpenLatestConversationAsync(page);
        await Assertions.Expect(LastAnswer(page).Locator(".md-answer table")).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(LastAnswer(page).Locator("[data-tool='explain_approval_chain']")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Stop_keeps_partial_answer()
    {
        fixture.RequireReady();
        await fixture.WaitForSeededPolicyAsync();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await Pages.LogInAsync(fixture, page, AppFixture.DemoUser, "/app/assistant");
        await Assertions.Expect(page.GetByTestId("demo-model")).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });

        await AskAsync(page, ApproversQuestion);

        // Some words are on screen (the Fake writes one every 60 ms here), then Stop.
        var text = LastAnswer(page).Locator(".md-answer");
        await Assertions.Expect(text).ToContainTextAsync("approvals", new() { Timeout = Pages.Timeout });
        await page.GetByTestId("stop").ClickAsync();

        await Assertions.Expect(LastAnswer(page).GetByTestId("message-status")).ToHaveTextAsync("Stopped", new() { Timeout = Pages.Timeout });
        await Assertions.Expect(page.GetByTestId("send")).ToBeVisibleAsync();
        var partial = (await text.InnerTextAsync()).Trim();
        partial.ShouldNotBeEmpty();
        partial.ShouldNotContain("From explain_approval_chain", customMessage: "stopped before the end of the answer");

        // The partial answer was saved as stopped: it is still there after a reload.
        await page.ReloadAsync();
        await OpenLatestConversationAsync(page);
        await Assertions.Expect(LastAnswer(page).GetByTestId("message-status")).ToHaveTextAsync("Stopped", new() { Timeout = Pages.Timeout });
        (await LastAnswer(page).Locator(".md-answer").InnerTextAsync()).Trim().ShouldStartWith(partial[..Math.Min(40, partial.Length)]);
    }

    [Fact]
    public async Task Admin_uploads_policy_and_sees_progress_to_ready()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await Pages.LogInAsync(fixture, page, "admin@acme.test", "/app/admin/policies");

        // A policy of 60 short sections: enough chunks for the embedding progress to show.
        var name = $"travel-policy-{Guid.NewGuid():N}"[..22];
        var markdown = new StringBuilder("# Travel policy\n\n");
        for (var i = 1; i <= 60; i++)
        {
            markdown.Append($"## {i} Rule {i}\n\nTravel rule {i}: book economy class for trips under {i + 2} hours and keep every receipt.\n\n");
        }

        await page.GetByTestId("upload").Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = name + ".md",
            MimeType = "text/markdown",
            Buffer = Encoding.UTF8.GetBytes(markdown.ToString()),
        });

        // The new document is listed at once, first waiting or working, then Ready with its chunks.
        var row = page.Locator("tr[data-document]").Filter(new() { HasText = name });
        var status = row.Locator("[data-cell='status'] [data-status]");
        await Assertions.Expect(row).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(status).ToHaveAttributeAsync("data-status", new Regex("^(Queued|Checking|Embedding|Ready)$"));
        await Assertions.Expect(status).ToHaveAttributeAsync("data-status", "Ready", new() { Timeout = Pages.Timeout });
        await Assertions.Expect(status).ToHaveTextAsync("Ready");
        await Assertions.Expect(row.Locator("[data-cell='chunks']")).ToHaveTextAsync(new Regex(@"^\s*\d+\s*$"));
        await Assertions.Expect(row.Locator("[data-progress]")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Tom_scans_invoice_reviews_and_saves()
    {
        fixture.RequireReady();

        // The seeded PO-1001 (plan 06 ruling 3): Tom's docks and stands from Fabrikam, received, waiting for Fabrikam's paper invoice.
        var orders = (await fixture.CallAsync(HttpMethod.Get, "/api/v1/purchasing/orders?pageSize=100", AppFixture.TomId)).Body.GetProperty("items");
        var orderNumber = "PO-1001";
        var orderId = orders.EnumerateArray().Single(o => o.GetProperty("number").GetString() == orderNumber).GetProperty("id").GetGuid();

        // Fabrikam's invoice for it, as a PDF (the README's sample with a fresh invoice number).
        var invoiceNumber = $"FAB-E2E-{Random.Shared.Next(100_000, 999_999)}";
        var pdf = InvoicePdf.FabrikamPo1001(invoiceNumber);

        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await Pages.LogInAsync(fixture, page, AppFixture.DemoUser, "/app/invoices");
        await page.Locator("app-invoice-scan-panel input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "invoice.pdf",
            MimeType = "application/pdf",
            Buffer = pdf,
        });

        var summary = page.GetByTestId("scan-summary");
        await Assertions.Expect(summary).ToContainTextAsync(invoiceNumber, new() { Timeout = Pages.Timeout });
        await Assertions.Expect(summary).ToContainTextAsync("Fabrikam Office");
        await Assertions.Expect(summary).ToContainTextAsync("confidence high");
        await page.GetByTestId("review-scan").ClickAsync();

        // The form is prefilled: the order the invoice names is chosen, the number and the line's quantity are in.
        await page.WaitForURLAsync(new Regex("/app/invoices/new\\?scan="), new() { Timeout = Pages.Timeout });
        await Assertions.Expect(page.GetByTestId("scan-note")).ToContainTextAsync(invoiceNumber);
        await Assertions.Expect(page.Locator("#invoice-order")).ToHaveValueAsync(orderId.ToString(), new() { Timeout = Pages.Timeout });
        await Assertions.Expect(page.GetByTestId("scan-note")).Not.ToContainTextAsync("not among the orders you can see");
        await Assertions.Expect(page.Locator("#invoice-number")).ToHaveValueAsync(invoiceNumber);
        await Assertions.Expect(page.GetByLabel("Quantity invoiced, USB-C Dock")).ToHaveValueAsync("2");
        await Assertions.Expect(page.GetByLabel("Quantity invoiced, Laptop Stand")).ToHaveValueAsync("2");

        await page.GetByTestId("save-invoice").GetByRole(AriaRole.Button).ClickAsync();

        // Saved: the invoice's own page, for Tom's order.
        await page.WaitForURLAsync(new Regex("/app/invoices/[0-9a-f-]{36}$"), new() { Timeout = Pages.Timeout });
        await Assertions.Expect(page.Locator("main")).ToContainTextAsync(invoiceNumber, new() { Timeout = Pages.Timeout });
        await Assertions.Expect(page.Locator("main")).ToContainTextAsync(orderNumber);
    }
}
