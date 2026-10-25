using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Application.Mapping;
using ProcureAi.Access.Domain;

namespace ProcureAi.Access.Application.Queries;

/// <summary>Asks what UserId may do, holding Roles (the roles of the token) in the tenant of the caller.</summary>
public sealed record GetEffectiveAccess(string UserId, IReadOnlyList<string> Roles);

public static class GetEffectiveAccessHandler
{
    public static async Task<EffectiveAccessDto> Handle(GetEffectiveAccess query, IAccessDbContext db, CancellationToken cancellationToken)
    {
        var matrix = await db.PermissionMatrices.AsNoTracking().Include(m => m.Grants).SingleOrDefaultAsync(cancellationToken);
        var user = await db.TenantUsers.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == query.UserId, cancellationToken);
        var departments = await db.Departments.AsNoTracking().ToListAsync(cancellationToken);
        var costCenters = await db.CostCenters.AsNoTracking().ToListAsync(cancellationToken);
        return AccessMapper.ToDto(AccessResolver.Resolve(matrix, user, query.UserId, query.Roles, departments, costCenters));
    }
}
