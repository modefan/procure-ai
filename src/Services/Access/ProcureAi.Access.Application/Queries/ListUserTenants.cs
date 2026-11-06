using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;

namespace ProcureAi.Access.Application.Queries;

/// <summary>Every tenant in which <paramref name="UserId"/> is a user (a <c>tenant_users</c> row), with the tenant's name.</summary>
public sealed record ListUserTenants(string UserId);

public static class ListUserTenantsHandler
{
    /// <summary>
    /// Cross-tenant on purpose (the tenant query filter does not apply): the tenant switcher needs every membership of one user. A
    /// tenant without a name row gets one from its organization (<see cref="TenantNames"/>), else is shown under its ID. Ordered by name.
    /// </summary>
    public static async Task<IReadOnlyList<TenantDto>> Handle(
        ListUserTenants query, IAccessDbContext db, IIdentityDirectory identities, CancellationToken cancellationToken)
    {
        var ids = await db.TenantUsers.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.UserId == query.UserId)
            .Select(u => u.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var names = await TenantNames.GetAsync(ids, db, identities, cancellationToken);
        return [.. ids.Select(id => new TenantDto(id, names.GetValueOrDefault(id, id))).OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal)];
    }
}
