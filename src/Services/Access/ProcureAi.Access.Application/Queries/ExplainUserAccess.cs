using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Access.Application.Queries;

public sealed record ExplainUserAccess(string UserId, string TenantId);

public static class ExplainUserAccessHandler
{
    /// <summary>Why the stored user has the permissions they have (from their stored roles, not a token). 404 when the tenant has no such user.</summary>
    public static async Task<AccessExplanationDto> Handle(ExplainUserAccess query, IAccessDbContext db, CancellationToken cancellationToken)
    {
        var user = await db.TenantUsers.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == query.UserId, cancellationToken)
            ?? throw new NotFoundException($"User {query.UserId} was not found.");
        var matrix = await db.PermissionMatrices.AsNoTracking().Include(m => m.Grants).SingleOrDefaultAsync(cancellationToken);
        var departments = await db.Departments.AsNoTracking().ToListAsync(cancellationToken);
        var costCenters = await db.CostCenters.AsNoTracking().ToListAsync(cancellationToken);
        var effective = AccessResolver.Resolve(matrix, user, user.UserId, user.Roles, departments, costCenters);
        return new AccessExplanationDto(
            user.UserId, user.DisplayName, query.TenantId, [.. user.Roles.Order(StringComparer.Ordinal)],
            AccessResolver.PermissionSources(matrix, user.Roles), effective.CostCenterCodes,
            [.. departments.Where(d => d.HeadUserId == user.UserId).Select(d => d.Code).Order(StringComparer.Ordinal)],
            user.ApprovalLimit.Amount, user.IsFinanceApprover);
    }
}
