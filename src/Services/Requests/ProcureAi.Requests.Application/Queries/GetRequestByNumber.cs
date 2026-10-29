using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Queries;

/// <summary>One request of the caller's tenant by its number (<c>PR-1043</c>, with or without the zero padding).</summary>
public sealed record GetRequestByNumber(string Number);

public static class GetRequestByNumberHandler
{
    /// <summary>The request, scoped like <see cref="GetRequestHandler"/> (the tenant filter); a text that is no request number finds nothing (404).</summary>
    public static async Task<RequestDto> Handle(GetRequestByNumber query, IRequestsDbContext db, CancellationToken cancellationToken)
    {
        RequestNumber number;
        try
        {
            number = RequestNumber.Parse(query.Number);
        }
        catch (DomainException)
        {
            throw new NotFoundException($"Request {query.Number} was not found.");
        }

        var request = await db.PurchaseRequests.AsNoTracking().Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Number == number, cancellationToken)
            ?? throw new NotFoundException($"Request {number} was not found.");
        return RequestMapper.ToDto(request);
    }
}
