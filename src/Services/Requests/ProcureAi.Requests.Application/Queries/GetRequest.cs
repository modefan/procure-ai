using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Mapping;

namespace ProcureAi.Requests.Application.Queries;

/// <summary>
/// One request of the caller's tenant, limited to the caller's scope like the list (<see cref="RequestScope"/>);
/// <paramref name="WholeTenant"/> as in <see cref="ListRequests"/>.
/// </summary>
public sealed record GetRequest(Guid Id, bool WholeTenant = false);

public static class GetRequestHandler
{
    /// <summary>Missing, another tenant's and out of the caller's scope are all 404, so the answer does not tell that it exists.</summary>
    public static async Task<RequestDto> Handle(
        GetRequest query, IRequestsDbContext db, ICallerScope scope, CancellationToken cancellationToken)
    {
        var callerScope = await scope.GetAsync(cancellationToken);
        var request = await db.PurchaseRequests.AsNoTracking().VisibleTo(callerScope, query.WholeTenant).Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == query.Id, cancellationToken)
            ?? throw new NotFoundException($"Request {query.Id} was not found.");
        return RequestMapper.ToDto(request);
    }
}
