using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Domain;

namespace ProcureAi.Access.Application.Queries;

/// <summary>
/// The names tenants are shown under. A <c>tenants</c> row holds it; the demo seed writes the demo tenants' rows, and any other tenant (a
/// real deployment, a company added in Keycloak later) gets its row the first time Access needs its name: the name of the identity
/// provider's organization with that alias (plan 07 carry-over 14). A tenant without an organization, or while the identity provider
/// cannot be asked, is shown under its ID and nothing is stored, so the next read asks again.
/// </summary>
public static class TenantNames
{
    /// <summary>The longest name stored (the <c>tenants.name</c> column).</summary>
    public const int MaxNameLength = 200;

    public static async Task<string> GetAsync(string tenantId, IAccessDbContext db, IIdentityDirectory identities, CancellationToken cancellationToken) =>
        (await GetAsync([tenantId], db, identities, cancellationToken)).GetValueOrDefault(tenantId, tenantId);

    /// <summary>The name of each of <paramref name="tenantIds"/> (an empty ID is left out).</summary>
    public static async Task<IReadOnlyDictionary<string, string>> GetAsync(
        IReadOnlyCollection<string> tenantIds, IAccessDbContext db, IIdentityDirectory identities, CancellationToken cancellationToken)
    {
        var ids = tenantIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        var names = await db.Tenants.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        var found = new List<Tenant>();
        foreach (var id in ids.Where(id => !names.ContainsKey(id)))
        {
            var name = (await identities.FindOrganizationNameAsync(id, cancellationToken))?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                names[id] = id;
                continue;
            }

            names[id] = name[..Math.Min(name.Length, MaxNameLength)];
            found.Add(Tenant.Create(id, names[id]));
        }

        if (found.Count > 0)
        {
            await StoreAsync(found, db, cancellationToken);
        }

        return names;
    }

    private static async Task StoreAsync(List<Tenant> found, IAccessDbContext db, CancellationToken cancellationToken)
    {
        db.Tenants.AddRange(found);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A parallel read stored the same tenant first (primary key): the name is the same organization's, so nothing is lost. Clear what
            // was tracked (the rows and the audit entry of the failed save), so a later save of this scope does not try again; the next read
            // finds the stored row.
            db.ChangeTracker.Clear();
        }
    }
}
