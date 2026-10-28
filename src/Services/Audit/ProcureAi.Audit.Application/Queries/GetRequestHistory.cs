using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;

namespace ProcureAi.Audit.Application.Queries;

/// <summary>The business events of one purchase request of the caller tenant, oldest first.</summary>
public sealed record GetRequestHistory(Guid RequestId);

public static class GetRequestHistoryHandler
{
    /// <summary>
    /// 404 when the caller tenant has no event about the request (another tenant's request looks the same as none).
    /// Display names come from Access (cached); when Access cannot answer in time, the summaries use user IDs.
    /// </summary>
    public static async Task<IReadOnlyList<BusinessEventDto>> Handle(
        GetRequestHistory query, ICallerContext caller, IAuditStore store, IAccessDirectory access, IOptions<AuditOptions> options,
        ILogger<GetRequestHistory> logger, CancellationToken cancellationToken)
    {
        var tenant = caller.TenantId ?? throw new NotFoundException($"No history for request {query.RequestId}.");
        var events = await store.ForSubjectAsync(tenant, query.RequestId, cancellationToken);
        if (events.Count == 0)
        {
            throw new NotFoundException($"No history for request {query.RequestId}.");
        }

        var history = BusinessEventSummaries.Read(events);
        var names = await DisplayNamesAsync(history.UserIds, tenant, access, options.Value.DisplayNameTimeout, logger, cancellationToken);
        return [.. history.Events.Select(e => new BusinessEventDto(e.Stored.Type, e.Stored.UserId, e.Stored.OccurredAt, history.Summarize(e, names)))];
    }

    internal static async Task<IReadOnlyDictionary<string, string>> DisplayNamesAsync(
        IReadOnlyCollection<string> userIds, string tenant, IAccessDirectory access, TimeSpan timeout, ILogger logger,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var found = await Task.WhenAll(userIds.Select(id => DisplayNameAsync(id, tenant, access, logger, deadline.Token, cancellationToken)));
        return found.Where(f => f.Name is not null).ToDictionary(f => f.Id, f => f.Name!, StringComparer.Ordinal);
    }

    private static async Task<(string Id, string? Name)> DisplayNameAsync(
        string id, string tenant, IAccessDirectory access, ILogger logger, CancellationToken deadline, CancellationToken request)
    {
        try
        {
            return (id, await access.GetDisplayNameAsync(id, tenant, deadline));
        }
        catch (Exception exception) when (!request.IsCancellationRequested)
        {
            // Access down or too slow: the history still answers, with the user ID.
            logger.LogWarning(exception, "No display name for {UserId} in {Tenant}; the history shows the ID", id, tenant);
            return (id, null);
        }
    }
}
