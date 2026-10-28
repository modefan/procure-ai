using System.Text.Json;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Access;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Invoicing;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Contracts.Requests;

namespace ProcureAi.Audit.Application.Queries;

/// <summary>A stored event and its contract (null when the contract no longer exists or the payload cannot be read).</summary>
public sealed record ReadEvent(BusinessEvent Stored, IIntegrationEvent? Contract);

/// <summary>
/// Turns the stored events of one request into sentences, e.g. "Approved by Maria Manager (step 1, Manager)". The step number
/// and level of a step come from its <c>ApprovalRequested</c> in the same history. Amounts in Rand (<c>R 30 000,00</c>).
/// </summary>
public sealed class BusinessEventSummaries
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<Guid, ApprovalRequested> _steps;

    private BusinessEventSummaries(IReadOnlyList<ReadEvent> events)
    {
        Events = events;
        _steps = events.Select(e => e.Contract).OfType<ApprovalRequested>()
            .GroupBy(r => r.StepId).ToDictionary(g => g.Key, g => g.Last());
        UserIds = events.SelectMany(UsersOf).Where(id => id != SystemCaller.Id).Distinct(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<ReadEvent> Events { get; }

    /// <summary>Every user the summaries name, to look up once.</summary>
    public IReadOnlyCollection<string> UserIds { get; }

    public static BusinessEventSummaries Read(IEnumerable<BusinessEvent> stored) =>
        new([.. stored.Select(e => new ReadEvent(e, Deserialize(e)))]);

    /// <summary>The approval step number an approval event is about; null for every other event.</summary>
    public int? StepNoOf(ReadEvent e) => e.Contract switch
    {
        ApprovalRequested r => r.StepNo,
        ApprovalStepApproved a => _steps.TryGetValue(a.StepId, out var step) ? step.StepNo : null,
        ApprovalReminderDue r => _steps.TryGetValue(r.StepId, out var step) ? step.StepNo : null,
        ApprovalAlertRaised a => _steps.TryGetValue(a.StepId, out var step) ? step.StepNo : null,
        _ => null,
    };

    public string Summarize(ReadEvent e, IReadOnlyDictionary<string, string> names)
    {
        string Name(string? id) => id is null || id == SystemCaller.Id ? "the system" : names.GetValueOrDefault(id, id);
        string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(Name)) is { Length: > 0 } joined ? joined : "nobody";
        static string Rand(decimal amount) => Money.Zar(amount).ToDisplayString();
        string Step(Guid stepId) => _steps.TryGetValue(stepId, out var step) ? $" (step {step.StepNo}, {Level(step.Level)})" : "";

        return e.Contract switch
        {
            RequestSubmitted s => $"Submitted by {Name(s.RequesterId)}: {s.Title} ({Rand(s.TotalInclVat)} incl. VAT, {s.CostCenterCode})",
            RequestCancelled c => $"Cancelled by {Name(c.CancelledBy)}" + (string.IsNullOrWhiteSpace(c.Reason) ? "" : $": {c.Reason}"),
            BudgetReserved r => $"{Rand(r.Amount)} reserved on {r.CostCenterCode} ({r.FiscalYear})",
            BudgetReservationFailed f =>
                $"Budget refused on {f.CostCenterCode}: {f.Reason} (requested {Rand(f.Requested)}, available {Rand(f.Available)})",
            BudgetReleased r => $"{Rand(r.Amount)} released on {r.CostCenterCode}",
            ApprovalRequested r => $"Approval requested from {Names(r.ApproverIds)} (step {r.StepNo}, {Level(r.Level)})",
            ApprovalStepApproved a => $"Approved by {Name(a.ApprovedBy)}{Step(a.StepId)}",
            ApprovalReminderDue r => $"Reminder {r.ReminderNo} sent to {Names(r.ApproverIds)}{Step(r.StepId)}",
            ApprovalEscalated x => $"Escalated from {Level(x.FromLevel)} to {Level(x.ToLevel)}: {Names(x.ApproverIds)}",
            ApprovalAlertRaised a => $"Alert{Step(a.StepId)}: {a.Reason}",
            RequestApproved a => $"Request approved ({Rand(a.TotalInclVat)} incl. VAT)",
            RequestRejected r => $"Rejected by {Name(r.RejectedBy)}: {r.Reason}",
            PurchaseOrderCreated o => $"Order {o.OrderNumber} created with supplier {o.SupplierId} ({Rand(o.TotalInclVat)} incl. VAT)",
            PurchaseOrderConfirmed o => $"Order {o.OrderNumber} confirmed by the supplier ({o.SupplierReference})"
                + (o.ExpectedDelivery is { } due ? $", delivery expected {due:yyyy-MM-dd}" : ""),
            PurchaseOrderRejected o => $"Order {o.OrderNumber} rejected by the supplier: {o.Reason}",
            GoodsReceived g => $"Goods received by {Name(g.ReceivedBy)} ({Units(g.Lines)})",
            PurchaseOrderClosedShort c => $"Order closed short by {Name(c.ClosedBy)}: {c.Reason} ({Units(c.ClosedLines)} outstanding)",
            PurchaseOrderCancelled c => (c.OrderId is null ? "Cancelled before an order existed" : "Order cancelled") + $": {c.Reason}",
            PurchaseOrderCancellationRefused c => $"Order cancellation refused: {c.Reason}",
            InvoiceReceived i => $"Invoice {i.SupplierInvoiceNumber} received from {i.SupplierId} via {i.Source} ({Rand(i.TotalInclVat)} incl. VAT)",
            InvoiceMatched => "Invoice matched to the order and the goods received",
            InvoiceMismatched m => $"Invoice does not match: {string.Join("; ", m.Reasons)}",
            PaymentReleased p => $"Payment {p.PaymentNumber} of {Rand(p.AmountInclVat)} released by {Name(p.ReleasedBy)}",
            PaymentExported p => $"Payment exported to the ERP ({p.ErpReference})",
            PurchaseOrderSettled s => $"Order settled: {Rand(s.PaidInclVat)} paid in total",
            RequestOrdered o => $"Order {o.OrderNumber} placed for the request",
            RequestCancellationConfirmed => "Cancellation of the ordered request confirmed",
            RequestCancellationRefused r => $"Cancellation of the ordered request refused: {r.Reason}",
            RequestCompleted c => $"Request completed ({Rand(c.SpentInclVat)} spent incl. VAT)",
            BudgetSpent b => $"{Rand(b.Amount)} spent on {b.CostCenterCode}",
            AccessChanged a => $"Access changed ({a.Kind})" + (a.UserId is null ? "" : $" for {Name(a.UserId)}"),
            _ => e.Stored.Type,
        };
    }

    private static string Units(IEnumerable<ReceivedLine> lines) =>
        string.Join(", ", lines.Select(l => $"line {l.LineNo}: {l.Quantity}"));

    private static string Level(string level) => level == "DepartmentHead" ? "Department head" : level;

    private static IEnumerable<string> UsersOf(ReadEvent e)
    {
        yield return e.Stored.UserId;
        IEnumerable<string?> named = e.Contract switch
        {
            RequestSubmitted s => new string?[] { s.RequesterId },
            PurchaseOrderCreated o => new string?[] { o.RequesterId },
            GoodsReceived g => new string?[] { g.ReceivedBy },
            PurchaseOrderClosedShort c => new string?[] { c.ClosedBy },
            PaymentReleased p => new string?[] { p.ReleasedBy },
            RequestCancelled c => new string?[] { c.CancelledBy },
            ApprovalRequested r => r.ApproverIds.ToArray<string?>(),
            ApprovalStepApproved a => new string?[] { a.ApprovedBy },
            ApprovalReminderDue r => r.ApproverIds.ToArray<string?>(),
            ApprovalEscalated x => x.ApproverIds.ToArray<string?>(),
            RequestRejected r => new string?[] { r.RejectedBy },
            AccessChanged a => new string?[] { a.UserId },
            _ => Array.Empty<string?>(),
        };
        foreach (var id in named)
        {
            if (!string.IsNullOrEmpty(id))
            {
                yield return id;
            }
        }
    }

    private static IIntegrationEvent? Deserialize(BusinessEvent e)
    {
        if (!IntegrationEventTypes.ByName.TryGetValue(e.Type, out var type))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(e.Payload, type, Json) as IIntegrationEvent;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
