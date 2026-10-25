using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;

namespace ProcureAi.Access.Application.Queries;

/// <summary>The permissions the given roles hold together in the tenant of the caller (used by the permission provider of this service).</summary>
public sealed record GetRolePermissions(IReadOnlyList<string> Roles);

public static class GetRolePermissionsHandler
{
    public static async Task<IReadOnlyList<string>> Handle(GetRolePermissions query, IAccessDbContext db, CancellationToken cancellationToken)
    {
        var matrix = await db.PermissionMatrices.AsNoTracking().Include(m => m.Grants).SingleOrDefaultAsync(cancellationToken);
        return matrix is null ? [] : [.. matrix.PermissionsFor(query.Roles)];
    }
}
