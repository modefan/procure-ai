using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Domain;

namespace ProcureAi.Access.Application.Queries;

/// <summary>Who the caller is in their tenant. <paramref name="Roles"/> are the roles of the caller's token.</summary>
public sealed record GetMe(string UserId, IReadOnlyList<string> Roles, string TenantId = "");

public static class GetMeHandler
{
    /// <summary>Uses the same rules as the internal effective-access lookup, then adds names (tenant query filters apply).</summary>
    public static async Task<MeDto> Handle(GetMe query, IAccessDbContext db, CancellationToken cancellationToken)
    {
        var matrix = await db.PermissionMatrices.AsNoTracking().Include(m => m.Grants).SingleOrDefaultAsync(cancellationToken);
        var user = await db.TenantUsers.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == query.UserId, cancellationToken);
        var departments = await db.Departments.AsNoTracking().ToListAsync(cancellationToken);
        var costCenters = await db.CostCenters.AsNoTracking().ToListAsync(cancellationToken);
        var access = AccessResolver.Resolve(matrix, user, query.UserId, query.Roles, departments, costCenters);

        var acting = costCenters.Where(c => access.CostCenterCodes.Contains(c.Code, StringComparer.Ordinal))
            .OrderBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new CostCenterDto(c.Code, c.Name, c.DepartmentCode))
            .ToList();
        decimal? limit = access.ApprovalLimit.Amount > 0 ? access.ApprovalLimit.Amount : null;
        var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == query.TenantId).Select(t => t.Name).SingleOrDefaultAsync(cancellationToken);
        return new MeDto(
            query.UserId, user?.DisplayName ?? query.UserId, [.. query.Roles.Order(StringComparer.Ordinal)], acting, limit,
            tenantName ?? query.TenantId);
    }
}
