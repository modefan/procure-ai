using System.Text.Json;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Access;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Budgets;
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
            AccessChanged a => $"Access changed ({a.Kind})" + (a.UserId is null ? "" : $" for {Name(a.UserId)}"),
            _ => e.Stored.Type,
        };
    }

    private static string Level(string level) => level == "DepartmentHead" ? "Department head" : level;

    private static IEnumerable<string> UsersOf(ReadEvent e)
    {
        yield return e.Stored.UserId;
        IEnumerable<string?> named = e.Contract switch
        {
            RequestSubmitted s => new string?[] { s.RequesterId },
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
