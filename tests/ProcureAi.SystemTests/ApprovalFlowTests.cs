using System.Diagnostics;
using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;

namespace ProcureAi.SystemTests;

/// <summary>
/// The whole system end to end, through the services' own APIs: submit, budget reservation, the approval saga with two steps,
/// the request following the outcome, and the audit (business events and data changes) of all of it. One collection, so the
/// tests run one after the other on the same budget.
/// </summary>
[Collection(SystemCollection.Name)]
public sealed class ApprovalFlowTests(SystemFixture system)
{
    private const string CostCenter = "IT-200";
    private const decimal TotalInclVat = 30_000.00m;

    // Messages travel through RabbitMQ, CDC through Debezium; attribution of an unmatched change waits Audit:Matching:Grace (30 s).
    private static readonly TimeSpan FlowTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AttributionTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task R30000_request_goes_from_submit_to_approved()
    {
        system.RequireReady();
        using var tomRequests = system.Service("requests", SystemFixture.Tom);
        using var tomBudgets = system.Service("budgets", SystemFixture.Tom);
        var before = await AvailableAsync(tomBudgets);

        // Tom raises the request as two lines, then edits it to one line of the same total, keeping the title (an edit that
        // changes no column of the request row itself), and submits: R 26 086,96 excl. VAT = R 30 000,00 incl.
        var (draft, draftTag) = await tomRequests.SendAsync<RequestView>(HttpMethod.Post, "/api/v1/requests", HttpStatusCode.Created, new
        {
            title = "Developer laptop",
            costCenterCode = CostCenter,
            supplierId = "sup-1",
            lines = new[] { new { sku = "LAP-15", description = "Laptop 15 inch", quantity = 2, unitPriceExclVat = 13_043.48m } },
        });
        var (edited, editedTag) = await tomRequests.SendAsync<RequestView>(HttpMethod.Put, $"/api/v1/requests/{draft.Id}", HttpStatusCode.OK, new
        {
            title = "Developer laptop",
            lines = new[] { new { sku = "LAP-15", description = "Laptop 15 inch", quantity = 1, unitPriceExclVat = 26_086.96m } },
        }, draftTag);
        edited.TotalInclVat.ShouldBe(TotalInclVat);
        var (submitted, _) = await tomRequests.SendAsync<RequestView>(
            HttpMethod.Post, $"/api/v1/requests/{draft.Id}/submit", HttpStatusCode.Accepted, ifMatch: editedTag);
        submitted.Status.ShouldBe("Submitted");

        // Maria (manager of IT-200) has it in her inbox and approves; over R 20 000 the department head approves next.
        await ApproveAsync(SystemFixture.Maria, draft.Id, expectedStep: 1, expectedLevel: "Manager");
        await ApproveAsync(SystemFixture.Dept, draft.Id, expectedStep: 2, expectedLevel: "DepartmentHead");

        var approved = await EventuallyAsync(
            () => tomRequests.GetAsync<RequestView>($"/api/v1/requests/{draft.Id}"), r => r.Body.Status == "Approved", "request approved");
        approved.Body.ApprovedAt.ShouldNotBeNull();
        (await EventuallyAsync(() => AvailableAsync(tomBudgets), a => a == before - TotalInclVat, "IT-200 lowered by R 30 000,00"))
            .ShouldBe(before - TotalInclVat);

        // Audit: every business event of the request, with readable summaries.
        using var tomAudit = system.Service("audit", SystemFixture.Tom);
        var history = await EventuallyAsync(
            () => HistoryAsync(tomAudit, draft.Id),
            h => h.Count(e => e.Type == "ApprovalStepApproved") == 2 && h.Any(e => e.Type == "RequestApproved"),
            "full history");
        history.Select(e => e.Type).Order().ShouldBe(
            new[] { "RequestSubmitted", "BudgetReserved", "ApprovalRequested", "ApprovalRequested", "ApprovalStepApproved", "ApprovalStepApproved", "RequestApproved" }.Order());
        history.ShouldContain(e => e.Type == "RequestSubmitted" && e.Summary.StartsWith("Submitted by Tom Requester: Developer laptop (R 30 000,00 incl. VAT"));
        history.ShouldContain(e => e.Type == "BudgetReserved" && e.Summary.StartsWith("R 30 000,00 reserved on IT-200"));
        history.ShouldContain(e => e.Type == "ApprovalStepApproved" && e.UserId == SystemFixture.Maria && e.Summary == "Approved by Maria Manager (step 1, Manager)");
        history.ShouldContain(e => e.Type == "ApprovalStepApproved" && e.UserId == SystemFixture.Dept && e.Summary == "Approved by Dept Head (step 2, Department head)");
        history.ShouldContain(e => e.Type == "RequestApproved" && e.Summary == "Request approved (R 30 000,00 incl. VAT)");

        // Data changes: every captured row change of the request is matched to the app user who caused it, none is "outside the app".
        var changes = await ResolvedDataChangesAsync(tomAudit, draft.Id);
        changes.ShouldAllBe(c => c.Attribution == "App", Describe(changes));
        changes.ShouldContain(c => c.Table == "purchase_requests" && c.Operation == "Insert" && c.UserId == SystemFixture.Tom);
        changes.ShouldContain(c => c.Table == "purchase_requests" && c.Operation == "Update" && c.UserId == SystemFixture.Tom);
        changes.ShouldContain(c => c.Table == "budget_reservations" && c.UserId == SystemFixture.Tom);
        changes.ShouldContain(c => c.Table == "approval_steps" && c.UserId == SystemFixture.Maria);
        changes.ShouldContain(c => c.Table == "approval_steps" && c.UserId == SystemFixture.Dept);
        await NoOutsideAppChangesAsync();
    }

    [Fact]
    public async Task Reject_returns_the_budget()
    {
        system.RequireReady();
        using var tomRequests = system.Service("requests", SystemFixture.Tom);
        using var tomBudgets = system.Service("budgets", SystemFixture.Tom);
        var before = await AvailableAsync(tomBudgets);

        var (draft, tag) = await tomRequests.SendAsync<RequestView>(HttpMethod.Post, "/api/v1/requests", HttpStatusCode.Created, new
        {
            title = "Second monitor",
            costCenterCode = CostCenter,
            supplierId = "sup-1",
            lines = new[] { new { sku = "MON-27", description = "Monitor 27 inch", quantity = 1, unitPriceExclVat = 26_086.96m } },
        });
        await tomRequests.SendAsync<RequestView>(HttpMethod.Post, $"/api/v1/requests/{draft.Id}/submit", HttpStatusCode.Accepted, ifMatch: tag);
        await EventuallyAsync(() => AvailableAsync(tomBudgets), a => a == before - TotalInclVat, "budget reserved");

        // Maria rejects at step 1: the request ends Rejected with her reason and the reservation is released.
        using var maria = system.Service("approvals", SystemFixture.Maria);
        var step = await InboxStepAsync(maria, draft.Id);
        await maria.SendAsync<StepView>(HttpMethod.Post, $"/api/v1/approvals/{step.Id}/reject", HttpStatusCode.OK,
            new { reason = "Not in this year's plan" }, $"\"{step.Version}\"");

        var rejected = await EventuallyAsync(
            () => tomRequests.GetAsync<RequestView>($"/api/v1/requests/{draft.Id}"), r => r.Body.Status == "Rejected", "request rejected");
        rejected.Body.RejectionReason.ShouldBe("Not in this year's plan");
        (await EventuallyAsync(() => AvailableAsync(tomBudgets), a => a == before, "budget returned")).ShouldBe(before);

        using var tomAudit = system.Service("audit", SystemFixture.Tom);
        var history = await EventuallyAsync(
            () => HistoryAsync(tomAudit, draft.Id), h => h.Any(e => e.Type == "BudgetReleased") && h.Any(e => e.Type == "RequestRejected"), "history");
        history.ShouldContain(e => e.Type == "RequestRejected" && e.UserId == SystemFixture.Maria && e.Summary == "Rejected by Maria Manager: Not in this year's plan");
        history.ShouldContain(e => e.Type == "BudgetReleased" && e.Summary.StartsWith("R 30 000,00 released on IT-200"));
        history.ShouldNotContain(e => e.Type == "RequestApproved");

        var changes = await ResolvedDataChangesAsync(tomAudit, draft.Id);
        changes.ShouldAllBe(c => c.Attribution == "App", Describe(changes));
        changes.ShouldContain(c => c.Table == "approval_steps" && c.UserId == SystemFixture.Maria);
        await NoOutsideAppChangesAsync();
    }

    [Fact]
    public async Task Every_resource_is_healthy_and_swagger_lists_five_definitions()
    {
        system.RequireReady();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var notifications = system.App.ResourceNotifications;
        var resources = system.Model.Resources
            .Where(r => r is ContainerResource or ProjectResource or ExecutableResource)
            // Aspire's hidden helpers that only run on demand (rebuild a project from the dashboard).
            .Where(r => r.GetType().Name != "ProjectRebuilderResource" && !r.Annotations.OfType<ExplicitStartupAnnotation>().Any())
            .ToList();
        resources.Count(r => r.Name.StartsWith("debezium-", StringComparison.Ordinal)).ShouldBe(4, "requestsdb, accessdb, budgetsdb, approvalsdb");

        // Run-to-completion resources (the <name>-migrate steps, the web app's npm install) must have finished with exit code 0;
        // everything else must be running and healthy.
        foreach (var resource in resources)
        {
            var settled = await notifications.WaitForResourceAsync(
                resource.Name,
                e => e.Snapshot.State?.Text is var state
                     && (state == KnownResourceStates.Finished || state == KnownResourceStates.Exited || state == KnownResourceStates.FailedToStart
                         || (state == KnownResourceStates.Running && e.Snapshot.HealthStatus == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy)),
                cts.Token);
            var snapshot = settled.Snapshot;
            if (snapshot.State?.Text == KnownResourceStates.Running)
            {
                continue;
            }

            snapshot.State?.Text.ShouldBe(KnownResourceStates.Finished, $"{resource.Name} state");
            snapshot.ExitCode.ShouldBe(0, $"{resource.Name} exit code");
        }

        // The gateway's Swagger UI offers one definition per service, and serves each document.
        using var gateway = system.Gateway();
        var config = await gateway.GetStringAsync("/swagger/index.js", TestContext.Current.CancellationToken);
        foreach (var service in SystemFixture.Services)
        {
            config.ShouldContain($"/openapi/{service}/v1.json");
            using var document = await gateway.GetAsync($"/openapi/{service}/v1.json", TestContext.Current.CancellationToken);
            document.StatusCode.ShouldBe(HttpStatusCode.OK, $"{service} document");
            (await document.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain($"/api/v1/{service}", Case.Sensitive, $"{service} document lists its API");
        }

        System.Text.RegularExpressions.Regex.Matches(config, @"/openapi/\w+/v1\.json").Select(m => m.Value).Distinct().Count().ShouldBe(5);
    }

    // ---- helpers ----

    private async Task ApproveAsync(string approver, Guid requestId, int expectedStep, string expectedLevel)
    {
        using var approvals = system.Service("approvals", approver);
        var step = await InboxStepAsync(approvals, requestId);
        step.StepNo.ShouldBe(expectedStep);
        step.Level.ShouldBe(expectedLevel);
        Plain(step.TotalInclVatDisplay).ShouldBe("R 30 000,00");
        step.CanAct.ShouldBeTrue(step.CannotActReason);
        var (acted, _) = await approvals.SendAsync<StepView>(
            HttpMethod.Post, $"/api/v1/approvals/{step.Id}/approve", HttpStatusCode.OK, ifMatch: $"\"{step.Version}\"");
        acted.Status.ShouldBe("Approved");
    }

    private static async Task<StepView> InboxStepAsync(ServiceClient approvals, Guid requestId)
    {
        var inbox = await EventuallyAsync(
            () => approvals.GetAsync<Paged<StepView>>("/api/v1/approvals/inbox?pageSize=100"),
            r => r.Body.Items.Any(s => s.RequestId == requestId),
            $"request {requestId} in the inbox");
        return inbox.Body.Items.Single(s => s.RequestId == requestId);
    }

    private static async Task<decimal> AvailableAsync(ServiceClient budgets)
    {
        var (check, _) = await budgets.GetAsync<BudgetCheck>($"/api/v1/budgets/{CostCenter}/check?amount=1");
        return check.Available;
    }

    private static async Task<IReadOnlyList<HistoryLine>> HistoryAsync(ServiceClient audit, Guid requestId)
    {
        using var response = await audit.SendAsync(HttpMethod.Get, $"/api/v1/audit/requests/{requestId}/history");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var lines = (await System.Text.Json.JsonSerializer.DeserializeAsync<List<HistoryLine>>(
            await response.Content.ReadAsStreamAsync(), ServiceClient.Json))!;
        return [.. lines.Select(l => l with { Summary = Plain(l.Summary) })];
    }

    /// <summary>The request's data changes once none is still waiting for its app row (matched, or flagged after the grace time).</summary>
    private static async Task<IReadOnlyList<DataChangeView>> ResolvedDataChangesAsync(ServiceClient audit, Guid requestId) =>
        await EventuallyAsync(
            async () =>
            {
                using var response = await audit.SendAsync(HttpMethod.Get, $"/api/v1/audit/requests/{requestId}/data-changes?pageSize=100");
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return (IReadOnlyList<DataChangeView>)[];
                }

                var page = (await System.Text.Json.JsonSerializer.DeserializeAsync<Paged<DataChangeView>>(
                    await response.Content.ReadAsStreamAsync(), ServiceClient.Json))!;
                return page.Items;
            },
            items => items.Count > 0 && items.All(c => c.Attribution != "Pending"),
            "data changes attributed",
            AttributionTimeout);

    /// <summary>
    /// No change anywhere in the system was flagged "changed outside the app": the platform view (admin key, no tenant) of every
    /// captured database, after every change is resolved. A false alert here means the app wrote a row without its audit row.
    /// </summary>
    private async Task NoOutsideAppChangesAsync()
    {
        using var platform = system.Service("audit", onBehalfOf: null, tenant: null);
        var all = await EventuallyAsync(
            async () => (await platform.GetAsync<Paged<DataChangeView>>("/api/v1/audit/data-changes?pageSize=100")).Body,
            p => p.Items.All(c => c.Attribution != "Pending"),
            "every data change attributed",
            AttributionTimeout);
        all.Total.ShouldBeLessThanOrEqualTo(all.Items.Count, "one page holds every change of the run");
        var outside = all.Items.Where(c => c.Attribution == "OutsideApp").ToList();
        outside.ShouldBeEmpty(Describe(outside));
    }

    /// <summary>Rand amounts are displayed with a non-breaking space between the thousands (en-ZA); compare with plain spaces.</summary>
    private static string Plain(string text) => text.Replace('\u00A0', ' ');

    private static string Describe(IEnumerable<DataChangeView> changes) =>
        string.Join("; ", changes.Select(c => $"{c.Database}.{c.Table} {c.Operation} {c.PrimaryKey} tx {c.TransactionId}: {c.Attribution} {c.UserId}"));

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        var limit = timeout ?? FlowTimeout;
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (clock.Elapsed > limit)
            {
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for: {what}. Last value: {System.Text.Json.JsonSerializer.Serialize(value)}");
            }

            // Reads are micro-cached for 1 s.
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
        }
    }

    private sealed record RequestView(Guid Id, string Number, string Status, decimal TotalInclVat, DateTimeOffset? ApprovedAt, string? RejectionReason, uint Version);

    private sealed record StepView(
        Guid Id, Guid RequestId, int StepNo, string Level, string Status, string TotalInclVatDisplay, bool CanAct, string? CannotActReason, uint Version);

    private sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

    private sealed record BudgetCheck(bool Fits, decimal Available, string FiscalYear);

    private sealed record HistoryLine(string Type, string UserId, DateTimeOffset OccurredAt, string Summary);

    private sealed record DataChangeView(
        string Id, string Database, string Table, string PrimaryKey, string Operation, long TransactionId, string Attribution, string? UserId, string? ActorId);
}
