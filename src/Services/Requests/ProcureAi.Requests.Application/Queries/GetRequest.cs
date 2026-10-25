using Microsoft.EntityFrameworkCore;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Mapping;

namespace ProcureAi.Requests.Application.Queries;

public sealed record GetRequest(Guid Id);

public static class GetRequestHandler
{
    public static async Task<RequestDto> Handle(GetRequest query, IRequestsDbContext db, CancellationToken cancellationToken)
    {
        var request = await db.PurchaseRequests.AsNoTracking().Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == query.Id, cancellationToken)
            ?? throw new NotFoundException($"Request {query.Id} was not found.");
        return RequestMapper.ToDto(request);
    }
}
