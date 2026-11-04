using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Requests;

namespace ProcureAi.Audit.Application.Queries;

/// <summary>
/// The latest business events of the caller tenant, newest first. Budget events (amounts of cost centers the caller may not see) and
/// invoice and payment events are left out unless the caller may see them all: <paramref name="IncludeBudgetEvents"/> is for
/// <c>budgets.manage</c>, <paramref name="IncludeFinanceEvents"/> for finance (<c>invoices.release-payment</c>) and the admin key.
/// Events about a request are listed only when the caller may read that request (<see cref="RequestReadScope"/>, plan 07 Task 5c;
/// <paramref name="WholeTenant"/> is decided by the Api, as for the Requests list).
/// </summary>
public sealed record GetActivity(int Limit, bool IncludeBudgetEvents, bool IncludeFinanceEvents, bool WholeTenant = false)
{
    /// <summary>The event types a caller without the wider view must not see, from the contract namespaces of Budgets and Invoicing.</summary>
    public IReadOnlyCollection<string> ExcludedTypes =>
        [.. (IncludeBudgetEvents ? [] : ActivityEventTypes.Budget), .. (IncludeFinanceEvents ? [] : ActivityEventTypes.Finance)];
}

/// <summary>Event type names by area, taken from the contracts so a new Budgets or Invoicing event is hidden by default.</summary>
public static class ActivityEventTypes
{
    public static IReadOnlyList<string> Budget { get; } = NamesIn("ProcureAi.Contracts.Budgets");

    public static IReadOnlyList<string> Finance { get; } = NamesIn("ProcureAi.Contracts.Invoicing");

    private static string[] NamesIn(string contractNamespace) =>
    [
        .. typeof(IIntegrationEvent).Assembly.GetTypes()
            .Where(t => t.Namespace == contractNamespace && t.IsClass && typeof(IIntegrationEvent).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal),
    ];
}

public static class GetActivityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Each event with its one-line summary, the request it is about (ID and number, the number from that request's
    /// <c>RequestSubmitted</c>) and the name of the user it happened for. Names come from Access (cached); when Access cannot
    /// answer in time the summaries use user IDs and <c>ActorName</c> is null.
    /// <para>
    /// The scope is applied before the limit: the newest events are read in pages of the limit and an event about a request the caller
    /// may not read (or whose <c>RequestSubmitted</c> Audit does not have: fail closed) is skipped, until the limit is filled or
    /// <c>Audit:RequestScope:ActivityScanLimit</c> events were looked at. Events about no request are listed as before.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<ActivityDto>> Handle(
        GetActivity query, ICallerContext caller, ICallerScope scope, IAuditStore store, IAccessDirectory access,
        IOptions<AuditOptions> options, ILogger<GetActivity> logger, CancellationToken cancellationToken)
    {
        var tenant = caller.TenantId ?? throw new NotFoundException("No tenant.");
        var callerScope = await scope.GetAsync(cancellationToken);
        var wide = callerScope.All || query.WholeTenant;
        var scanLimit = Math.Max(options.Value.RequestScope.ActivityScanLimit, query.Limit);
        var submissions = new Dictionary<Guid, Domain.BusinessEvent>();
        var shown = new List<Domain.BusinessEvent>();
        var seen = new HashSet<Guid>();
        var scanned = 0;
        while (shown.Count < query.Limit && scanned < scanLimit)
        {
            var take = Math.Min(query.Limit, scanLimit - scanned);
            var batch = await store.LatestAsync(tenant, scanned, take, query.ExcludedTypes, cancellationToken);
            scanned += batch.Count;
            await AddSubmissionsAsync(tenant, batch, submissions, store, cancellationToken);
            foreach (var stored in batch)
            {
                // An event written between two pages shifts the next page: never list one twice.
                if (shown.Count < query.Limit && seen.Add(stored.Id) && (wide || InScope(stored, submissions, callerScope)))
                {
                    shown.Add(stored);
                }
            }

            if (batch.Count < take)
            {
                break;
            }
        }

        if (shown.Count == 0)
        {
            return [];
        }

        var numbers = Numbers(submissions);
        var summaries = BusinessEventSummaries.Read(shown);
        var names = await GetRequestHistoryHandler.DisplayNamesAsync(
            summaries.UserIds, tenant, access, options.Value.DisplayNameTimeout, logger, cancellationToken);
        return
        [
            .. summaries.Events.Select(e => new ActivityDto(
                e.Stored.OccurredAt,
                e.Stored.Type,
                e.Stored.SubjectId,
                e.Stored.SubjectId is { } id ? numbers.GetValueOrDefault(id) : null,
                summaries.Summarize(e, names),
                names.GetValueOrDefault(e.Stored.UserId))),
        ];
    }

    private static bool InScope(Domain.BusinessEvent stored, Dictionary<Guid, Domain.BusinessEvent> submissions, CallerScope scope) =>
        stored.SubjectId is not { } id
        || (submissions.TryGetValue(id, out var submitted)
            && RequestReadScope.FromSubmission(submitted) is { } owner
            && scope.Allows(owner.RequesterId, owner.CostCenterCode));

    private static async Task AddSubmissionsAsync(
        string tenant, IReadOnlyList<Domain.BusinessEvent> events, Dictionary<Guid, Domain.BusinessEvent> submissions, IAuditStore store,
        CancellationToken cancellationToken)
    {
        var missing = events.Where(e => e.SubjectId is { } id && !submissions.ContainsKey(id)).Select(e => e.SubjectId!.Value).Distinct().ToList();
        foreach (var stored in await store.SubmissionsAsync(tenant, missing, cancellationToken))
        {
            if (stored.SubjectId is { } id)
            {
                submissions.TryAdd(id, stored);
            }
        }
    }

    private static Dictionary<Guid, string> Numbers(Dictionary<Guid, Domain.BusinessEvent> submissions)
    {
        var numbers = new Dictionary<Guid, string>();
        foreach (var (id, stored) in submissions)
        {
            if (TryNumber(stored.Payload) is { } number)
            {
                numbers[id] = number;
            }
        }

        return numbers;
    }

    private static string? TryNumber(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<RequestSubmitted>(payload, Json)?.Number;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
