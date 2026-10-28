using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Shouldly;

namespace ProcureAi.SystemTests;

/// <summary>
/// The steps the system tests share: raising and approving a request, waiting for the flow to reach a state, reading budgets, the audit
/// history and the data changes. Every call goes to a service's own API with the admin key on behalf of a demo user.
/// </summary>
public sealed class SystemFlow(SystemFixture system)
{
    public const string CostCenter = "IT-200";
    public const string Supplier = "contoso-hardware";

    // Messages travel through RabbitMQ, CDC through Debezium; attribution of an unmatched change waits Audit:Matching:Grace (30 s).
    public static readonly TimeSpan FlowTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AttributionTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Tom raises a request for one catalog item from contoso-hardware on IT-200 and submits it.</summary>
    public async Task<RequestView> SubmitAsync(string title, string sku, int quantity, decimal expectedTotalInclVat)
    {
        using var tom = system.Service("requests", SystemFixture.Tom);
        var (draft, tag) = await tom.SendAsync<RequestView>(HttpMethod.Post, "/api/v1/requests", HttpStatusCode.Created, new
        {
            title,
            costCenterCode = CostCenter,
            supplierId = Supplier,
            lines = new[] { new { sku, quantity } },
        });
        draft.TotalInclVat.ShouldBe(expectedTotalInclVat);
        var (submitted, _) = await tom.SendAsync<RequestView>(HttpMethod.Post, $"/api/v1/requests/{draft.Id}/submit", HttpStatusCode.Accepted, ifMatch: tag);
        submitted.Status.ShouldBe("Submitted");
        return submitted;
    }

    /// <summary>The approver finds the request's open step in their inbox and approves it.</summary>
    public async Task ApproveAsync(string approver, Guid requestId, int expectedStep, string expectedLevel, string expectedTotalDisplay)
    {
        using var approvals = system.Service("approvals", approver);
        var step = await InboxStepAsync(approvals, requestId);
        step.StepNo.ShouldBe(expectedStep);
        step.Level.ShouldBe(expectedLevel);
        Plain(step.TotalInclVatDisplay).ShouldBe(expectedTotalDisplay);
        step.CanAct.ShouldBeTrue(step.CannotActReason);
        var (acted, _) = await approvals.SendAsync<StepView>(
            HttpMethod.Post, $"/api/v1/approvals/{step.Id}/approve", HttpStatusCode.OK, ifMatch: $"\"{step.Version}\"");
        acted.Status.ShouldBe("Approved");
    }

    public static async Task<StepView> InboxStepAsync(ServiceClient approvals, Guid requestId)
    {
        var inbox = await EventuallyAsync(
            () => approvals.GetAsync<Paged<StepView>>("/api/v1/approvals/inbox?pageSize=100"),
            r => r.Body.Items.Any(s => s.RequestId == requestId),
            $"request {requestId} in the inbox");
        return inbox.Body.Items.Single(s => s.RequestId == requestId);
    }

    /// <summary>Waits until Tom's request satisfies <paramref name="done"/> (e.g. a status) and returns it with its ETag.</summary>
    public async Task<(RequestView Body, string? ETag)> RequestWhenAsync(Guid requestId, Func<RequestView, bool> done, string what, TimeSpan? timeout = null)
    {
        using var tom = system.Service("requests", SystemFixture.Tom);
        return await EventuallyAsync(() => tom.GetAsync<RequestView>($"/api/v1/requests/{requestId}"), r => done(r.Body), what, timeout);
    }

    /// <summary>Waits until the order of the request (as Tom sees it) satisfies <paramref name="done"/>; 404 while it does not exist yet.</summary>
    public async Task<(OrderView Body, string? ETag)> OrderWhenAsync(Guid requestId, Func<OrderView, bool> done, string what, TimeSpan? timeout = null)
    {
        using var tom = system.Service("purchasing", SystemFixture.Tom);
        return await EventuallyAsync(
            () => tom.GetAsync<OrderView>($"/api/v1/purchasing/requests/{requestId}/order"), r => done(r.Body), what, timeout, retryNotFound: true);
    }

    public async Task<decimal> AvailableAsync()
    {
        using var budgets = system.Service("budgets", SystemFixture.Tom);
        var (check, _) = await budgets.GetAsync<BudgetCheck>($"/api/v1/budgets/{CostCenter}/check?amount=1");
        return check.Available;
    }

    /// <summary>IT-200's budget of the current fiscal year (amount, reserved, spent, available).</summary>
    public async Task<BudgetView> BudgetAsync()
    {
        using var budgets = system.Service("budgets", SystemFixture.Tom);
        var (check, _) = await budgets.GetAsync<BudgetCheck>($"/api/v1/budgets/{CostCenter}/check?amount=1");
        var (budget, _) = await budgets.GetAsync<BudgetView>($"/api/v1/budgets/{CostCenter}/{check.FiscalYear}");
        return budget;
    }

    public async Task<IReadOnlyList<HistoryLine>> HistoryAsync(Guid requestId)
    {
        using var audit = system.Service("audit", SystemFixture.Tom);
        using var response = await audit.SendAsync(HttpMethod.Get, $"/api/v1/audit/requests/{requestId}/history");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        await EnsureOkAsync(response, $"/api/v1/audit/requests/{requestId}/history");
        var lines = (await JsonSerializer.DeserializeAsync<List<HistoryLine>>(await response.Content.ReadAsStreamAsync(), ServiceClient.Json))!;
        return [.. lines.Select(l => l with { Summary = Plain(l.Summary) })];
    }

    /// <summary>Waits until the request's audit history satisfies <paramref name="done"/>.</summary>
    public Task<IReadOnlyList<HistoryLine>> HistoryWhenAsync(Guid requestId, Func<IReadOnlyList<HistoryLine>, bool> done, string what) =>
        EventuallyAsync(() => HistoryAsync(requestId), done, what);

    /// <summary>The request's data changes once none is still waiting for its app row (matched, or flagged after the grace time).</summary>
    public async Task<IReadOnlyList<DataChangeView>> ResolvedDataChangesAsync(Guid requestId, Func<IReadOnlyList<DataChangeView>, bool>? complete = null)
    {
        using var audit = system.Service("audit", SystemFixture.Tom);
        return await EventuallyAsync(
            async () => (await AllPagesAsync(audit, $"/api/v1/audit/requests/{requestId}/data-changes", allowNotFound: true)).Items,
            items => items.Count > 0 && items.All(c => c.Attribution != "Pending") && (complete?.Invoke(items) ?? true),
            "data changes attributed",
            AttributionTimeout);
    }

    /// <summary>
    /// No change anywhere in the system was flagged "changed outside the app": the platform view (admin key, no tenant) of every
    /// captured database, after every change is resolved. A false alert here means the app wrote a row without its audit row.
    /// </summary>
    public async Task NoOutsideAppChangesAsync()
    {
        using var platform = system.Service("audit", onBehalfOf: null, tenant: null);
        var all = await EventuallyAsync(
            () => AllPagesAsync(platform, "/api/v1/audit/data-changes"),
            p => p.Items.All(c => c.Attribution != "Pending"),
            "every data change attributed",
            AttributionTimeout);
        var outside = all.Items.Where(c => c.Attribution == "OutsideApp").ToList();
        outside.ShouldBeEmpty(Describe(outside));
    }

    /// <summary>Reads every page of a paged audit list (the first page's total says when to stop); 404 gives an empty list when allowed.</summary>
    private static async Task<(IReadOnlyList<DataChangeView> Items, int Total)> AllPagesAsync(ServiceClient audit, string path, bool allowNotFound = false)
    {
        const int pageSize = 100;
        var items = new List<DataChangeView>();
        var total = 0;
        for (var page = 1; ; page++)
        {
            using var response = await audit.SendAsync(HttpMethod.Get, $"{path}?page={page}&pageSize={pageSize}");
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return ([], 0);
            }

            await EnsureOkAsync(response, path);
            var body = (await JsonSerializer.DeserializeAsync<Paged<DataChangeView>>(await response.Content.ReadAsStreamAsync(), ServiceClient.Json))!;
            items.AddRange(body.Items);
            total = body.Total;
            if (body.Items.Count == 0 || items.Count >= total)
            {
                return (items, total);
            }
        }
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, string path)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpStatusException(response.RequestMessage?.Method ?? HttpMethod.Get, path, response.StatusCode,
                $"expected 200, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    /// <summary>Rand amounts are displayed with a non-breaking space between the thousands (en-ZA); compare with plain spaces.</summary>
    public static string Plain(string text) => text.Replace(' ', ' ');

    public static string Describe(IEnumerable<DataChangeView> changes) =>
        string.Join("; ", changes.Select(c => $"{c.Database}.{c.Table} {c.Operation} {c.PrimaryKey} tx {c.TransactionId}: {c.Attribution} {c.UserId}"));

    /// <summary>
    /// Polls <paramref name="read"/> until <paramref name="done"/> holds or the deadline passes. Transient failures (a service restarting,
    /// 502/503/504) are retried until the deadline, and so is a 404 when <paramref name="retryNotFound"/> (something not created yet).
    /// </summary>
    public static async Task<T> EventuallyAsync<T>(
        Func<Task<T>> read, Func<T, bool> done, string what, TimeSpan? timeout = null, bool retryNotFound = false)
    {
        var clock = Stopwatch.StartNew();
        var limit = timeout ?? FlowTimeout;
        while (true)
        {
            try
            {
                var value = await read();
                if (done(value))
                {
                    return value;
                }

                if (clock.Elapsed > limit)
                {
                    throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for: {what}. Last value: {JsonSerializer.Serialize(value)}");
                }
            }
            catch (Exception e) when (e is HttpStatusException { IsTransient: true } or HttpRequestException
                                      || (retryNotFound && e is HttpStatusException { Status: HttpStatusCode.NotFound }))
            {
                if (clock.Elapsed > limit)
                {
                    throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for: {what}. Last error: {e.Message}", e);
                }
            }

            // Reads are micro-cached for 1 s.
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
        }
    }
}

public sealed record RequestView(
    Guid Id, string Number, string Status, decimal TotalInclVat, DateTimeOffset? ApprovedAt, string? RejectionReason, Guid? OrderId,
    string? OrderNumber, decimal? SpentInclVat, string? CancellationRefusedReason, uint Version);

public sealed record StepView(
    Guid Id, Guid RequestId, int StepNo, string Level, string Status, string TotalInclVatDisplay, bool CanAct, string? CannotActReason, uint Version);

public sealed record OrderView(
    Guid Id, Guid RequestId, string Number, string Status, string SupplierStatus, string? SupplierReference, IReadOnlyList<OrderLineView> Lines,
    decimal TotalInclVat, string? CancelReason, uint Version);

public sealed record OrderLineView(int LineNo, string Sku, int Ordered, int Received, int ClosedShort, int Outstanding);

public sealed record InvoiceView(
    Guid Id, Guid OrderId, Guid RequestId, string SupplierId, string SupplierInvoiceNumber, string Source, string Status,
    IReadOnlyList<string> MismatchReasons, IReadOnlyList<InvoiceLineView> Lines, IReadOnlyList<PaymentView> Payments, decimal TotalInclVat, uint Version);

public sealed record InvoiceLineView(int LineNo, string Sku, int Quantity, int Paid, bool? PriceOk, bool? QuantityOk, int? Received, string? MatchReason);

public sealed record PaymentView(Guid Id, string Number, decimal TotalInclVat, string ReleasedBy, string ExportStatus, string? ErpReference, int ExportAttempts);

public sealed record ErpPaymentView(string DocumentNumber, string PaymentReference, string VendorCode, string InvoiceNumber, string Currency, long GrossAmountCents);

public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record BudgetCheck(bool Fits, decimal Available, string FiscalYear);

public sealed record BudgetView(string CostCenterCode, string FiscalYear, decimal Amount, decimal Reserved, decimal Spent, decimal Available);

public sealed record HistoryLine(string Type, string UserId, DateTimeOffset OccurredAt, string Summary);

public sealed record DataChangeView(
    string Id, string Database, string Table, string PrimaryKey, string Operation, long TransactionId, string Attribution, string? UserId, string? ActorId);
