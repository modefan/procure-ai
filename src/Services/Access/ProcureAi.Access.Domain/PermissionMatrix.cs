using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Access.Domain;

/// <summary>
/// The role to permission matrix of one tenant. Roles come from the identity provider; what a role may do is data here,
/// editable at runtime, so code only ever checks permissions.
/// </summary>
public sealed class PermissionMatrix
{
    /// <summary>The role that administers the matrix. It must always keep <see cref="LockoutGuardPermission"/>.</summary>
    public const string AdminRole = "admin";

    public const string LockoutGuardPermission = Permissions.AdminAuthorization;

    private readonly List<PermissionGrant> _grants = [];

    private PermissionMatrix()
    {
    }

    public string TenantId { get; private set; } = "";

    /// <summary>When the matrix was last changed. A real column, so every change writes the row and moves <see cref="Version"/>.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token (the Postgres <c>xmin</c> system column), set by the database on every write.</summary>
    public uint Version { get; private set; }

    public IReadOnlyList<PermissionGrant> Grants => _grants.AsReadOnly();

    /// <summary>The roles that have at least one permission, in a stable order.</summary>
    public IReadOnlyList<string> Roles =>
        [.. _grants.Select(g => g.Role).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public static PermissionMatrix Create(
        string tenantId, IReadOnlyDictionary<string, IReadOnlyCollection<string>> rolePermissions, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new DomainException("Tenant is required.");
        }

        var matrix = new PermissionMatrix { TenantId = tenantId, UpdatedAt = now };
        foreach (var (role, permissions) in rolePermissions)
        {
            matrix.SetRolePermissions(role, permissions, now);
        }

        return matrix;
    }

    public IReadOnlySet<string> PermissionsOf(string role) =>
        _grants.Where(g => g.Role == role).Select(g => g.Permission).ToHashSet(StringComparer.Ordinal);

    /// <summary>The union of the permissions of all the given roles. A role the matrix does not know gives nothing.</summary>
    public IReadOnlySet<string> PermissionsFor(IEnumerable<string> roles)
    {
        var wanted = roles.ToHashSet(StringComparer.Ordinal);
        return _grants.Where(g => wanted.Contains(g.Role)).Select(g => g.Permission).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Replaces the permissions of a role (only the grants that change are touched) and records when.</summary>
    public void SetRolePermissions(string role, IEnumerable<string> permissions, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new DomainException("Role is required.");
        }

        var wanted = permissions.ToHashSet(StringComparer.Ordinal);
        var unknown = wanted.Where(p => !Permissions.All.Contains(p)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new DomainException($"Unknown permission: {string.Join(", ", unknown)}.");
        }

        if (role == AdminRole && !wanted.Contains(LockoutGuardPermission))
        {
            throw new DomainException($"Removing {LockoutGuardPermission} from {AdminRole} would lock admins out.");
        }

        _grants.RemoveAll(g => g.Role == role && !wanted.Contains(g.Permission));
        var existing = PermissionsOf(role);
        foreach (var permission in wanted.Order(StringComparer.Ordinal).Where(p => !existing.Contains(p)))
        {
            _grants.Add(new PermissionGrant(role, permission));
        }

        UpdatedAt = now;
    }
}
