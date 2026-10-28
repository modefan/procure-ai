using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Queries;

public sealed record ListRequests(int Page, int PageSize, RequestStatus? Status);

public static class ListRequestsHandler
{
    /// <summary>Newest first (highest request number), optionally filtered by status.</summary>
    public static async Task<PagedResult<RequestDto>> Handle(
        ListRequests query, IRequestsDbContext db, CancellationToken cancellationToken)
    {
        var requests = db.PurchaseRequests.AsNoTracking();
        if (query.Status is { } status)
        {
            requests = requests.Where(r => r.Status == status);
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
