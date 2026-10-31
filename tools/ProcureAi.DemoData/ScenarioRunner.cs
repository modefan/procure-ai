using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using ProcureAi.Contracts;

namespace ProcureAi.DemoData;

/// <summary>What one scenario run did: how many requests it created (0 on a repeated run) and found, and what failed.</summary>
public sealed record SeedResult(string Tenant, int Created, int Found, IReadOnlyList<ScenarioFailedException> Failures)
{
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>A request of a scenario could not reach its stop. The message names the tenant, the request and the step.</summary>
public sealed class ScenarioFailedException(string tenant, string key, string step, string reason, Exception? inner = null)
    : Exception($"{tenant}/{key} failed at step '{step}': {reason}", inner)
{
    public string Tenant { get; } = tenant;

    public string Key { get; } = key;

    public string Step { get; } = step;
}

/// <summary>
/// Drives every request of a scenario to its stop through the public APIs, as the people in it (see <see cref="GatewayClient"/>).
/// <list type="bullet">
/// <item>Idempotent across runs: a request is looked up by title and requester first and resumed from where it is; every write is sent
/// with the Idempotency-Key <c>demo:&lt;tenant&gt;:&lt;key&gt;:&lt;step&gt;</c>, and a step whose effect is already there is skipped.</item>
/// <item>Asynchronous transitions (budget reservation, approval steps, the order, the supplier's webhooks, the invoice match, settlement)
/// are awaited by polling, each within <see cref="DemoDataOptions.StepTimeout"/>; there are no fixed sleeps.</item>
/// <item>Requests are created one by one in file order (their numbers follow the file), then the first one that gets an order is driven on
/// its own, so its order is the tenant's first (PO-1001 on fresh data, plan 06 ruling 3); the rest run in parallel, at most
/// <see cref="DemoDataOptions.MaxParallel"/> at a time.</item>
/// </list>
/// </summary>
public sealed class ScenarioRunner(GatewayClient gateway, IOptions<DemoDataOptions> options, TimeProvider time, ILogger<ScenarioRunner> logger)
{
    public static readonly ActivitySource Activities = new("ProcureAi.DemoData");

    private static readonly TimeZoneInfo Johannesburg = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    private DemoDataOptions Settings => options.Value;

    public async Task<SeedResult> RunAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        using var activity = Activities.StartActivity($"seed {scenario.Tenant}");
        var clock = Stopwatch.StartNew();
        var failures = new ConcurrentQueue<ScenarioFailedException>();
        var created = 0;
        var requests = new Dictionary<string, RequestView>();

        // Created one by one, in file order: the request numbers follow the file.
        try
        {
            foreach (var request in scenario.Requests)
            {
                var (view, isNew) = await EnsureCreatedAsync(Of(scenario, request), cancellationToken);
                requests[request.Key] = view;
                created += isNew ? 1 : 0;
            }
        }
        catch (ScenarioFailedException e)
        {
            return new SeedResult(scenario.Tenant, created, requests.Count - created, [e]);
        }

        async Task DriveSafelyAsync(ScenarioRequest request, CancellationToken token)
        {
            var context = Of(scenario, request);
            context.Id = requests[request.Key].Id;
            using var span = Activities.StartActivity($"seed {scenario.Tenant}/{request.Key}");
            try
            {
                var final = await DriveAsync(context, requests[request.Key], token);
                logger.LogInformation("{Tenant}/{Key}: {Number} is {Status} (stop {StopAt})", scenario.Tenant, request.Key, final.Number, final.Status, request.StopAt);
            }
            catch (ScenarioFailedException e)
            {
                failures.Enqueue(e);
                logger.LogError("{Message}", e.Message);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                var failure = new ScenarioFailedException(scenario.Tenant, request.Key, "unexpected", e.Message, e);
                failures.Enqueue(failure);
                logger.LogError(e, "{Message}", failure.Message);
            }
        }

        // The first request that is ordered goes first and alone, so its order is the tenant's first.
        var first = scenario.Requests.FirstOrDefault(r => r.IsOrdered);
        if (first is not null)
        {
            await DriveSafelyAsync(first, cancellationToken);
        }

        await Parallel.ForEachAsync(
            scenario.Requests.Where(r => r != first),
            new ParallelOptions { MaxDegreeOfParallelism = Settings.MaxParallel, CancellationToken = cancellationToken },
            async (request, token) => await DriveSafelyAsync(request, token));

        logger.LogInformation(
            "{Tenant}: {Count} requests in {Seconds:0} s ({Created} created, {Failed} failed)",
            scenario.Tenant, scenario.Requests.Count, clock.Elapsed.TotalSeconds, created, failures.Count);
        return new SeedResult(scenario.Tenant, created, scenario.Requests.Count - created, [.. failures]);
    }

    private static RequestContext Of(Scenario scenario, ScenarioRequest request) => new(scenario, request);

    // ---- Create and submit --------------------------------------------------------------------------------------------------

    /// <summary>The request with this title by this requester, or a new draft.</summary>
    private async Task<(RequestView View, bool Created)> EnsureCreatedAsync(RequestContext c, CancellationToken ct)
    {
        const string step = "create";
        if (await FindAsync(c, ct) is { } existing)
        {
            return (existing, false);
        }

        var r = c.Request;
        var response = await SendAsync(c, step, HttpMethod.Post, c.Requester, "/api/v1/requests", new
        {
            title = r.Title,
            costCenterCode = r.CostCenter,
            supplierId = r.Supplier,
            lines = r.Lines.Select(l => new { sku = l.Sku, quantity = l.Quantity }),
        }, ct: ct);
        var view = Read<RequestView>(c, step, response, HttpStatusCode.Created, HttpStatusCode.OK);
        logger.LogInformation("{Tenant}/{Key}: created {Number}", c.Tenant, r.Key, view.Number);
        return (view, true);
    }

    private async Task<RequestView?> FindAsync(RequestContext c, CancellationToken ct)
    {
        for (var page = 1; ; page++)
        {
            var list = await ReadAsync<Paged<RequestView>>(c, "create", c.Requester, $"/api/v1/requests?mine=true&page={page}&pageSize=100", ct);
            if (list.Items.FirstOrDefault(r => r.Title == c.Request.Title && r.RequesterId == c.Requester) is { } found)
            {
                return found;
            }

            if (list.Items.Count == 0 || page * list.PageSize >= list.Total)
            {
                return null;
            }
        }
    }

    private async Task<RequestView> DriveAsync(RequestContext c, RequestView request, CancellationToken ct)
    {
        var r = c.Request;
        if (r.StopAt == StopAt.Draft)
        {
            return request;
        }

        request = await SubmitAsync(c, request, ct);
        switch (r.StopAt)
        {
            case StopAt.Cancelled:
                return await CancelAsync(c, request, ct);
            case StopAt.Rejected when r.RejectReason is null:
                // Budgets cannot reserve it: the saga rejects it by itself.
                return await RequestWhenAsync(c, "rejected over budget", x => x.Status == "Rejected", ct);
        }

        request = await ApproveAsync(c, ct);
        if (!r.IsOrdered)
        {
            return request;
        }

        var order = await OrderWhenAsync(c, "order confirmed by the supplier", o => o.Status is not ("PendingSupplier" or "SentToSupplier"), ct);
        if (order.Status is "Rejected" or "Cancelled")
        {
            throw Fail(c, "order confirmed by the supplier", $"{order.Number} is {order.Status}");
        }

        if (r.StopAt == StopAt.Ordered)
        {
            return await CurrentAsync(c, ct);
        }

        order = await ReceiveAsync(c, order, ct);
        if (r.StopAt is StopAt.PartiallyReceived or StopAt.Received)
        {
            return await CurrentAsync(c, ct);
        }

        var invoice = await InvoiceAsync(c, order, ct);
        switch (r.StopAt)
        {
            case StopAt.Invoiced:
                await InvoiceWhenAsync(c, order, "invoice matched", i => i.Status == "Matched", ct);
                return await CurrentAsync(c, ct);
            case StopAt.Mismatched:
                await InvoiceWhenAsync(c, order, "invoice mismatched", i => i.Status == "Mismatched", ct);
                return await CurrentAsync(c, ct);
            case StopAt.PartiallyPaid:
                await PayAsync(c, order, invoice, PayMode.Partial, ct);
                await InvoiceWhenAsync(c, order, "invoice partially paid", i => i.Status == "PartiallyPaid", ct);
                return await CurrentAsync(c, ct);
            case StopAt.ClosedShort:
                await PayAsync(c, order, invoice, PayMode.Full, ct);
                await CloseShortAsync(c, order, ct);
                return await RequestWhenAsync(c, "request completed", x => x.Status == "Completed", ct);
            default:
                await PayAsync(c, order, invoice, PayMode.Full, ct);
                return await RequestWhenAsync(c, "request completed", x => x.Status == "Completed", ct);
        }
    }

    private async Task<RequestView> SubmitAsync(RequestContext c, RequestView request, CancellationToken ct)
    {
        const string step = "submit";
        if (request.Status != "Draft")
        {
            return request;
        }

        await ActAsync(c, step, async () =>
        {
            var current = await CurrentAsync(c, ct);
            return current.Status != "Draft"
                ? null
                : await SendAsync(c, step, HttpMethod.Post, c.Requester, $"/api/v1/requests/{current.Id}/submit", ifMatch: current.Version, ct: ct);
        }, ct, HttpStatusCode.Accepted, HttpStatusCode.OK);
        return await RequestWhenAsync(c, step, x => x.Status != "Draft", ct);
    }

    /// <summary>The requester cancels once the approval has started (the budget is reserved), so the release shows in the history.</summary>
    private async Task<RequestView> CancelAsync(RequestContext c, RequestView request, CancellationToken ct)
    {
        const string step = "cancel";
        if (request.Status is "Cancelled" or "Cancelling")
        {
            return await RequestWhenAsync(c, "request cancelled", x => x.Status == "Cancelled", ct);
        }

        await PendingStepAsync(c, ct);
        await ActAsync(c, step, async () =>
        {
            var current = await CurrentAsync(c, ct);
            return current.Status is "Cancelled" or "Cancelling"
                ? null
                : await SendAsync(c, step, HttpMethod.Post, c.Requester, $"/api/v1/requests/{current.Id}/cancel",
                    new { reason = c.Request.CancelReason }, current.Version, ct);
        }, ct, HttpStatusCode.OK);
        return await RequestWhenAsync(c, "request cancelled", x => x.Status == "Cancelled", ct);
    }

    // ---- Approval ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Approves the open steps in order, each as the approver the scenario names for it, until the request waits where it stops (an
    /// open step, or one at the department-head or finance level) or is approved. A Rejected request's last named approver rejects.
    /// </summary>
    private async Task<RequestView> ApproveAsync(RequestContext c, CancellationToken ct)
    {
        var r = c.Request;
        var approvers = r.Approvers ?? [];
        while (true)
        {
            var (request, pending) = await PendingStepAsync(c, ct);
            switch (request.Status)
            {
                case "Rejected" when r.StopAt == StopAt.Rejected:
                    return request;
                case "Approved" or "Ordered" or "Completed" when r.IsOrdered:
                    return request;
                case not "Submitted":
                    throw Fail(c, "approval", $"{request.Number} is {request.Status}, but the scenario stops at {r.StopAt}");
            }

            var waitsHere = r.StopAt switch
            {
                StopAt.Submitted => true,
                StopAt.AwaitingDepartmentHead => pending!.Level == "DepartmentHead",
                StopAt.AwaitingFinance => pending!.Level == "Finance",
                _ => false,
            };
            if (waitsHere)
            {
                return request;
            }

            var stepNo = pending!.StepNo;
            var index = stepNo - 1;
            if (index >= approvers.Count)
            {
                throw Fail(c, $"approve step {stepNo}", $"no approver named for step {stepNo} ({pending.Level})");
            }

            var approver = UserId(approvers[index]);
            var reject = r.StopAt == StopAt.Rejected && index == approvers.Count - 1;
            var step = $"{(reject ? "reject" : "approve")} step {stepNo}";
            await ActAsync(c, step, async () =>
            {
                // The chain as the approver sees it: whether they may act (and why not) and the step's version.
                var chain = await ChainAsync(c, approver, ct);
                var open = chain?.Steps.FirstOrDefault(s => s.StepNo == stepNo && s.Status == "Pending");
                if (open is null)
                {
                    return null;
                }

                if (!open.CanAct)
                {
                    throw Fail(c, step, $"{approvers[index]} cannot act: {open.CannotActReason}");
                }

                return reject
                    ? await SendAsync(c, step, HttpMethod.Post, approver, $"/api/v1/approvals/{open.Id}/reject", new { reason = r.RejectReason },
                        open.Version, ct, $"reject-{stepNo}")
                    : await SendAsync(c, step, HttpMethod.Post, approver, $"/api/v1/approvals/{open.Id}/approve", ifMatch: open.Version, ct: ct,
                        keyStep: $"approve-{stepNo}");
            }, ct, HttpStatusCode.OK);

            await WaitAsync(c, $"{step} recorded", () => ChainAsync(c, c.Requester, ct),
                chain => chain is not null && chain.Steps.Where(s => s.StepNo == stepNo).All(s => s.Status != "Pending"), ct);
        }
    }

    /// <summary>
    /// Waits until the request is past approval or has an open step (the saga reserved the budget and planned the chain), and returns both.
    /// </summary>
    private async Task<(RequestView Request, StepView? Pending)> PendingStepAsync(RequestContext c, CancellationToken ct)
    {
        var (request, chain) = await WaitAsync(c, "approval step opened", async () => (await CurrentAsync(c, ct), await ChainAsync(c, c.Requester, ct)),
            x => x.Item1.Status != "Submitted" || x.Item2?.Steps.Any(s => s.Status == "Pending") == true, ct);
        return (request, chain?.Steps.FirstOrDefault(s => s.Status == "Pending"));
    }

    private async Task<ChainView?> ChainAsync(RequestContext c, string actAs, CancellationToken ct)
    {
        var response = await gateway.SendAsync(HttpMethod.Get, c.Tenant, actAs, $"/api/v1/approvals/requests/{c.Id}", cancellationToken: ct);
        return response.Status == HttpStatusCode.NotFound ? null : response.Read<ChainView>(HttpStatusCode.OK);
    }

    // ---- Order: receive and close short -----------------------------------------------------------------------------------

    /// <summary>The requester receives what the scenario says (all of it by default), less what has arrived already.</summary>
    private async Task<OrderView> ReceiveAsync(RequestContext c, OrderView order, CancellationToken ct)
    {
        const string step = "receive";
        var target = order.Lines.ToDictionary(
            l => l.LineNo,
            l => c.Request.Receive is { } part ? part.Where(p => p.Sku == l.Sku).Sum(p => p.Quantity) : l.Ordered);
        if (order.Status != "ClosedShort" && order.Lines.Any(l => l.Received < target[l.LineNo]))
        {
            await ActAsync(c, step, async () =>
            {
                var current = await OrderAsync(c, ct);
                var lines = current.Lines.Where(l => l.Received < target[l.LineNo])
                    .Select(l => new { lineNo = l.LineNo, quantity = target[l.LineNo] - l.Received }).ToList();
                return lines.Count == 0
                    ? null
                    : await SendAsync(c, step, HttpMethod.Post, c.Requester, $"/api/v1/purchasing/orders/{current.Id}/receipts",
                        new { lines, deliveryNote = $"DN-{current.Number}" }, current.Version, ct);
            }, ct, HttpStatusCode.OK);
        }

        return await OrderWhenAsync(c, "goods received", o => o.Lines.All(l => l.Received >= target[l.LineNo]), ct);
    }

    private async Task CloseShortAsync(RequestContext c, OrderView order, CancellationToken ct)
    {
        const string step = "close short";
        await ActAsync(c, step, async () =>
        {
            var current = await OrderAsync(c, ct);
            return current.Status == "ClosedShort"
                ? null
                : await SendAsync(c, step, HttpMethod.Post, c.Requester, $"/api/v1/purchasing/orders/{order.Id}/close-short",
                    new { reason = c.Request.CloseShortReason }, current.Version, ct);
        }, ct, HttpStatusCode.OK);
        await OrderWhenAsync(c, "order closed short", o => o.Status == "ClosedShort", ct);
    }

    // ---- Invoice and payment ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Finance types in the paper invoice (suppliers that send none), or the supplier's own invoice is awaited; either way until it is
    /// matched against what has been received.
    /// </summary>
    private async Task<InvoiceView> InvoiceAsync(RequestContext c, OrderView order, CancellationToken ct)
    {
        const string step = "invoice";
        if (c.Request.Invoice is { } paper && (await InvoicesAsync(c, order, ct)).Count == 0)
        {
            var delta = paper.PriceDelta ?? 0m;
            var lines = order.Lines.Select(l => new { lineNo = l.LineNo, quantity = l.Ordered, unitPriceExclVat = l.UnitPriceExclVat + delta }).ToList();
            var exclVat = lines.Sum(l => l.quantity * l.unitPriceExclVat);
            var total = exclVat + Math.Round(exclVat * 0.15m, 2, MidpointRounding.AwayFromZero);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Johannesburg).DateTime);
            await ActAsync(c, step, async () => (await InvoicesAsync(c, order, ct)).Count > 0
                ? null
                : await SendAsync(c, step, HttpMethod.Post, c.Finance, "/api/v1/invoicing/invoices", new
                {
                    orderId = order.Id,
                    // The supplier's own numbering, e.g. FAB-1005 for Fabrikam's invoice of PO-1005.
                    supplierInvoiceNumber = $"{c.Request.Supplier[..3].ToUpperInvariant()}-{order.Number.Replace("PO-", "", StringComparison.Ordinal)}",
                    invoiceDate = today,
                    lines,
                    statedTotalInclVat = total,
                }, ct: ct), ct, HttpStatusCode.OK);
        }

        var received = order.Lines.ToDictionary(l => l.LineNo, l => l.Received);
        return await InvoiceWhenAsync(c, order, "invoice matched against the receipts",
            i => i.Lines.All(l => l.Received is null || l.Received == received.GetValueOrDefault(l.LineNo)) && i.Status != "AwaitingOrder", ct);
    }

    /// <summary>Finance releases a payment: everything payable, or half of each line (rounded down). Skipped when a payment was made.</summary>
    private async Task PayAsync(RequestContext c, OrderView order, InvoiceView invoice, PayMode mode, CancellationToken ct)
    {
        const string step = "pay";
        if (invoice.Payments.Count > 0)
        {
            return;
        }

        await ActAsync(c, step, async () =>
        {
            var current = (await InvoicesAsync(c, order, ct)).Single();
            if (current.Payments.Count > 0)
            {
                return null;
            }

            object? body = mode == PayMode.Full
                ? null
                : new { lines = current.Lines.Where(l => l.Quantity >= 2).Select(l => new { lineNo = l.LineNo, quantity = l.Quantity / 2 }) };
            return await SendAsync(c, step, HttpMethod.Post, c.Finance, $"/api/v1/invoicing/invoices/{current.Id}/payments", body, current.Version, ct);
        }, ct, HttpStatusCode.OK);
    }

    private async Task<IReadOnlyList<InvoiceView>> InvoicesAsync(RequestContext c, OrderView order, CancellationToken ct) =>
        (await ReadAsync<Paged<InvoiceView>>(c, "invoice", c.Finance, $"/api/v1/invoicing/invoices?orderId={order.Id}", ct)).Items;

    private async Task<InvoiceView> InvoiceWhenAsync(RequestContext c, OrderView order, string what, Func<InvoiceView, bool> done, CancellationToken ct) =>
        (await WaitAsync(c, what, () => InvoicesAsync(c, order, ct), list => list.Count == 1 && done(list[0]), ct))[0];

    // ---- Reads and waits --------------------------------------------------------------------------------------------------

    private Task<RequestView> CurrentAsync(RequestContext c, CancellationToken ct) =>
        ReadAsync<RequestView>(c, "read request", c.Requester, $"/api/v1/requests/{c.Id}", ct);

    private Task<OrderView> OrderAsync(RequestContext c, CancellationToken ct) =>
        ReadAsync<OrderView>(c, "read order", c.Requester, $"/api/v1/purchasing/requests/{c.Id}/order", ct);

    private Task<RequestView> RequestWhenAsync(RequestContext c, string what, Func<RequestView, bool> done, CancellationToken ct) =>
        WaitAsync(c, what, () => CurrentAsync(c, ct), done, ct);

    private Task<OrderView> OrderWhenAsync(RequestContext c, string what, Func<OrderView, bool> done, CancellationToken ct) =>
        WaitAsync(c, what, () => OrderAsync(c, ct), done, ct, retryNotFound: true);

    private async Task<T> ReadAsync<T>(RequestContext c, string step, string actAs, string path, CancellationToken ct)
    {
        try
        {
            return await gateway.GetAsync<T>(c.Tenant, actAs, path, ct);
        }
        catch (GatewayException e) when (!e.IsTransient && e.Status != HttpStatusCode.NotFound)
        {
            throw Fail(c, step, e.Message, e);
        }
    }

    /// <summary>
    /// Polls <paramref name="read"/> until <paramref name="done"/> holds, within the step timeout. Transient failures (a service restarting,
    /// rate limiting, 502/503/504) are retried until then, and so is a 404 when <paramref name="retryNotFound"/> (not created yet).
    /// </summary>
    private async Task<T> WaitAsync<T>(
        RequestContext c, string what, Func<Task<T>> read, Func<T, bool> done, CancellationToken ct, bool retryNotFound = false)
    {
        var clock = Stopwatch.StartNew();
        string last;
        while (true)
        {
            try
            {
                var value = await read();
                if (done(value))
                {
                    return value;
                }

                last = System.Text.Json.JsonSerializer.Serialize(value, GatewayClient.Json);
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutRejectedException
                                      or TaskCanceledException { CancellationToken.IsCancellationRequested: false }
                                      or GatewayException { IsTransient: true }
                                      || (retryNotFound && e is GatewayException { Status: HttpStatusCode.NotFound })
                                      || (retryNotFound && e is ScenarioFailedException { InnerException: GatewayException { Status: HttpStatusCode.NotFound } }))
            {
                last = e.Message;
            }

            if (clock.Elapsed > Settings.StepTimeout)
            {
                throw Fail(c, what, $"timed out after {Settings.StepTimeout.TotalSeconds:0} s; last: {Shorten(last)}");
            }

            await Task.Delay(Settings.PollInterval, time, ct);
        }
    }

    /// <summary>
    /// Sends a write until it is accepted. <paramref name="send"/> reads the current state first and returns null when the write is no
    /// longer needed. A 412 (read a moment too early: reads are micro-cached for 1 s) or a transient failure is retried with a fresh read
    /// and the same Idempotency-Key; a 409 means the state moved on, which the caller's next wait checks.
    /// </summary>
    private async Task ActAsync(RequestContext c, string step, Func<Task<GatewayResponse?>> send, CancellationToken ct, params HttpStatusCode[] accepted)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            GatewayResponse? response;
            try
            {
                response = await send();
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutRejectedException or GatewayException { IsTransient: true })
            {
                response = null;
                if (clock.Elapsed > Settings.StepTimeout)
                {
                    throw Fail(c, step, e.Message, e);
                }

                await Task.Delay(Settings.PollInterval, time, ct);
                continue;
            }

            if (response is null || accepted.Contains(response.Status) || response.Status == HttpStatusCode.Conflict)
            {
                return;
            }

            var retry = response.Status == HttpStatusCode.PreconditionFailed || new GatewayException(response, "").IsTransient;
            if (!retry || clock.Elapsed > Settings.StepTimeout)
            {
                throw Fail(c, step, new GatewayException(response, $"expected {string.Join(" or ", accepted.Select(s => (int)s))}").Message);
            }

            await Task.Delay(Settings.PollInterval, time, ct);
        }
    }

    private Task<GatewayResponse> SendAsync(
        RequestContext c, string step, HttpMethod method, string actAs, string path, object? body = null, uint? ifMatch = null,
        CancellationToken ct = default, string? keyStep = null) =>
        gateway.SendAsync(method, c.Tenant, actAs, path, body, ifMatch, $"demo:{c.Tenant}:{c.Request.Key}:{keyStep ?? step.Replace(' ', '-')}", ct);

    private static T Read<T>(RequestContext c, string step, GatewayResponse response, params HttpStatusCode[] expected)
    {
        try
        {
            return response.Read<T>(expected);
        }
        catch (GatewayException e)
        {
            throw Fail(c, step, e.Message, e);
        }
    }

    private static ScenarioFailedException Fail(RequestContext c, string step, string reason, Exception? inner = null) =>
        new(c.Tenant, c.Request.Key, step, reason, inner);

    private static string Shorten(string text) => text.Length <= 400 ? text : text[..400] + "...";

    private static string UserId(string username) =>
        DemoUsers.All.TryGetValue(username, out var id) ? id : throw new InvalidOperationException($"{username} is not a demo user.");

    /// <summary>One request of a scenario while it is driven: its tenant, its people (as user IDs) and, once created, its ID.</summary>
    private sealed class RequestContext(Scenario scenario, ScenarioRequest request)
    {
        public string Tenant => scenario.Tenant;

        public ScenarioRequest Request => request;

        public string Requester { get; } = UserId(request.Requester);

        public string Finance { get; } = UserId(scenario.Finance);

        public Guid Id { get; set; }
    }
}
