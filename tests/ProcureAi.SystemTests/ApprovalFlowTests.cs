using System.Net;
using Shouldly;

namespace ProcureAi.SystemTests;

/// <summary>
/// The whole system end to end, through the services' own APIs: submit, budget reservation, the approval saga with two steps,
/// the request following the outcome, and the audit (business events and data changes) of all of it. One collection, so the
/// tests run one after the other on the same budget. Since plan 03 an approved request goes on to be ordered; these tests stop
/// looking at approval (OrderToPaymentTests follows the rest).
/// </summary>
[Collection(SystemCollection.Name)]
public sealed class ApprovalFlowTests(SystemFixture system)
{
    private const decimal TotalInclVat = 28_980.00m;

    private readonly SystemFlow _flow = new(system);

    [Fact]
    public async Task R28980_request_goes_from_submit_to_approved()
    {
        system.RequireReady();
        using var tomRequests = system.Service("requests", SystemFixture.Tom);
        var before = await _flow.AvailableAsync();

        // Tom raises the request as two lines, then edits it to one line of the same total, keeping the title (an edit that
        // changes no column of the request row itself), and submits: R 25 200,00 excl. VAT = R 28 980,00 incl. (LAP-14 of the demo catalog)
        var (draft, draftTag) = await tomRequests.SendAsync<RequestView>(HttpMethod.Post, "/api/v1/requests", HttpStatusCode.Created, new
        {
            title = "Developer laptop",
            costCenterCode = SystemFlow.CostCenter,
            supplierId = SystemFlow.Supplier,
            lines = new[] { new { sku = "LAP-14", quantity = 2 } },
        });
        var (edited, editedTag) = await tomRequests.SendAsync<RequestView>(HttpMethod.Put, $"/api/v1/requests/{draft.Id}", HttpStatusCode.OK, new
        {
            title = "Developer laptop",
            lines = new[] { new { sku = "LAP-14", quantity = 1 } },
        }, draftTag);
        edited.TotalInclVat.ShouldBe(TotalInclVat);
        var (submitted, _) = await tomRequests.SendAsync<RequestView>(
            HttpMethod.Post, $"/api/v1/requests/{draft.Id}/submit", HttpStatusCode.Accepted, ifMatch: editedTag);
        submitted.Status.ShouldBe("Submitted");

        // Maria (manager of IT-200) has it in her inbox and approves; over R 20 000 the department head approves next.
        await _flow.ApproveAsync(SystemFixture.Maria, draft.Id, expectedStep: 1, expectedLevel: "Manager", "R 28 980,00");
        await _flow.ApproveAsync(SystemFixture.Dept, draft.Id, expectedStep: 2, expectedLevel: "DepartmentHead", "R 28 980,00");

        // Approved, and right after that ordered (plan 03): either status shows the approval went through.
        var approved = await _flow.RequestWhenAsync(draft.Id, r => r.Status is "Approved" or "Ordered", "request approved");
        approved.Body.ApprovedAt.ShouldNotBeNull();
        (await SystemFlow.EventuallyAsync(_flow.AvailableAsync, a => a == before - TotalInclVat, "IT-200 lowered by R 28 980,00"))
            .ShouldBe(before - TotalInclVat);

        // Audit: every business event of the approval, with readable summaries (the ordering events that follow are not looked at here).
        var history = await _flow.HistoryWhenAsync(
            draft.Id,
            h => h.Count(e => e.Type == "ApprovalStepApproved") == 2 && h.Any(e => e.Type == "RequestApproved"),
            "full history");
        string[] approvalEvents = ["RequestSubmitted", "BudgetReserved", "ApprovalRequested", "ApprovalStepApproved", "RequestApproved"];
        history.Where(e => approvalEvents.Contains(e.Type)).Select(e => e.Type).Order().ShouldBe(
            new[] { "RequestSubmitted", "BudgetReserved", "ApprovalRequested", "ApprovalRequested", "ApprovalStepApproved", "ApprovalStepApproved", "RequestApproved" }.Order());
        history.ShouldContain(e => e.Type == "RequestSubmitted" && e.Summary.StartsWith("Submitted by Tom Mokoena: Developer laptop (R 28 980,00 incl. VAT"));
        history.ShouldContain(e => e.Type == "BudgetReserved" && e.Summary.StartsWith("R 28 980,00 reserved on IT-200"));
        history.ShouldContain(e => e.Type == "ApprovalStepApproved" && e.UserId == SystemFixture.Maria && e.Summary == "Approved by Maria Naidoo (step 1, Manager)");
        history.ShouldContain(e => e.Type == "ApprovalStepApproved" && e.UserId == SystemFixture.Dept && e.Summary == "Approved by Dept Head (step 2, Department head)");
        history.ShouldContain(e => e.Type == "RequestApproved" && e.Summary == "Request approved (R 28 980,00 incl. VAT)");

        // Data changes: every captured row change of the request is matched to the app user who caused it, none is "outside the app".
        var changes = await _flow.ResolvedDataChangesAsync(
            draft.Id, items => items.Count(c => c.Table == "approval_steps" && c.UserId is SystemFixture.Maria or SystemFixture.Dept) >= 2);
        changes.ShouldAllBe(c => c.Attribution == "App", SystemFlow.Describe(changes));
        changes.ShouldContain(c => c.Table == "purchase_requests" && c.Operation == "Insert" && c.UserId == SystemFixture.Tom);
        changes.ShouldContain(c => c.Table == "purchase_requests" && c.Operation == "Update" && c.UserId == SystemFixture.Tom);
        changes.ShouldContain(c => c.Table == "budget_reservations" && c.UserId == SystemFixture.Tom);
        changes.ShouldContain(c => c.Table == "approval_steps" && c.UserId == SystemFixture.Maria);
        changes.ShouldContain(c => c.Table == "approval_steps" && c.UserId == SystemFixture.Dept);
        await _flow.NoOutsideAppChangesAsync();
    }

    [Fact]
    public async Task Reject_returns_the_budget()
    {
        system.RequireReady();
        var before = await _flow.AvailableAsync();

        var request = await _flow.SubmitAsync("Second monitor", "LAP-14", 1, TotalInclVat);
        await SystemFlow.EventuallyAsync(_flow.AvailableAsync, a => a == before - TotalInclVat, "budget reserved");

        // Maria rejects at step 1: the request ends Rejected with her reason and the reservation is released.
        using var maria = system.Service("approvals", SystemFixture.Maria);
        var step = await SystemFlow.InboxStepAsync(maria, request.Id);
        await maria.SendAsync<StepView>(HttpMethod.Post, $"/api/v1/approvals/{step.Id}/reject", HttpStatusCode.OK,
            new { reason = "Not in this year's plan" }, $"\"{step.Version}\"");

        var rejected = await _flow.RequestWhenAsync(request.Id, r => r.Status == "Rejected", "request rejected");
        rejected.Body.RejectionReason.ShouldBe("Not in this year's plan");
        (await SystemFlow.EventuallyAsync(_flow.AvailableAsync, a => a == before, "budget returned")).ShouldBe(before);

        var history = await _flow.HistoryWhenAsync(
            request.Id, h => h.Any(e => e.Type == "BudgetReleased") && h.Any(e => e.Type == "RequestRejected"), "history");
        history.ShouldContain(e => e.Type == "RequestRejected" && e.UserId == SystemFixture.Maria && e.Summary == "Rejected by Maria Naidoo: Not in this year's plan");
        history.ShouldContain(e => e.Type == "BudgetReleased" && e.Summary.StartsWith("R 28 980,00 released on IT-200"));
        history.ShouldNotContain(e => e.Type == "RequestApproved");
        history.ShouldNotContain(e => e.Type == "PurchaseOrderCreated");

        var changes = await _flow.ResolvedDataChangesAsync(request.Id, items => items.Any(c => c.Table == "approval_steps" && c.UserId == SystemFixture.Maria));
        changes.ShouldAllBe(c => c.Attribution == "App", SystemFlow.Describe(changes));
        changes.ShouldContain(c => c.Table == "approval_steps" && c.UserId == SystemFixture.Maria);
        await _flow.NoOutsideAppChangesAsync();
    }
}
