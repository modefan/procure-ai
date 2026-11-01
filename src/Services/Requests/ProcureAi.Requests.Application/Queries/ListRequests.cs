using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Queries;

/// <summary>
/// What the list's <c>status</c> parameter accepts: one status (same names as <see cref="RequestStatus"/>) or a group.
/// <c>Open</c> is every status that can still change (Draft, Submitted, Approved, Ordered, Cancelling); <c>Closed</c> is
/// Completed, Rejected and Cancelled.
/// </summary>
public enum RequestStatusFilter
{
    Draft,
    Submitted,
    Approved,
    Ordered,
    Completed,
    Rejected,
    Cancelling,
    Cancelled,
    Open,
    Closed,
}

public static class RequestStatusFilters
{
    public static IReadOnlyList<RequestStatus> Statuses(this RequestStatusFilter filter) => filter switch
    {
        RequestStatusFilter.Open => [RequestStatus.Draft, RequestStatus.Submitted, RequestStatus.Approved, RequestStatus.Ordered, RequestStatus.Cancelling],
        RequestStatusFilter.Closed => [RequestStatus.Completed, RequestStatus.Rejected, RequestStatus.Cancelled],
        RequestStatusFilter.Draft => [RequestStatus.Draft],
        RequestStatusFilter.Submitted => [RequestStatus.Submitted],
        RequestStatusFilter.Approved => [RequestStatus.Approved],
        RequestStatusFilter.Ordered => [RequestStatus.Ordered],
        RequestStatusFilter.Completed => [RequestStatus.Completed],
        RequestStatusFilter.Rejected => [RequestStatus.Rejected],
        RequestStatusFilter.Cancelling => [RequestStatus.Cancelling],
        RequestStatusFilter.Cancelled => [RequestStatus.Cancelled],
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
    };
}

/// <summary><paramref name="RequesterId"/> limits the list to the requests that user made (the caller's own, for <c>mine=true</c>).</summary>
public sealed record ListRequests(int Page, int PageSize, RequestStatusFilter? Status, string? RequesterId = null);

public static class ListRequestsHandler
{
    /// <summary>Newest first (highest request number), limited to the caller's scope, optionally filtered by status (or group) and requester.</summary>
    public static async Task<PagedResult<RequestDto>> Handle(
        ListRequests query, IRequestsDbContext db, ICallerScope scope, CancellationToken cancellationToken)
    {
        var requests = db.PurchaseRequests.AsNoTracking();
        if (query.Status is { } filter)
        {
            var statuses = filter.Statuses();
            requests = requests.Where(r => statuses.Contains(r.Status));
        }

        if (query.RequesterId is { } requester)
        {
            requests = requests.Where(r => r.RequesterId == requester);
        }

        // Spec 4.2: the list holds the caller's own requests and those of the cost centers Access assigns to them (finance gets every
        // cost center there). The admin key and services acting for nobody see all of the tenant's requests. No extra permission.
        var callerScope = await scope.GetAsync(cancellationToken);
        if (!callerScope.All)
        {
            var userId = callerScope.UserId;
            var codes = callerScope.CostCenterCodes.ToArray();
            requests = requests.Where(r => r.RequesterId == userId || codes.Contains(r.CostCenterCode));
        }

        var total = await requests.CountAsync(cancellationToken);
        var page = await requests.Include(r => r.Lines)
            .OrderByDescending(r => r.Number)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<RequestDto>(page.Select(RequestMapper.ToDto).ToList(), query.Page, query.PageSize, total);
    }
}
