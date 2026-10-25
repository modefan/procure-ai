namespace ProcureAi.Access.Domain;

/// <summary>One permission granted to one role (a row of <c>permission_grants</c>). Owned by the tenant's <see cref="PermissionMatrix"/>.</summary>
public sealed class PermissionGrant
{
    private PermissionGrant()
    {
    }

    internal PermissionGrant(string role, string permission)
    {
        Role = role;
        Permission = permission;
    }

    public string Role { get; private set; } = "";

    public string Permission { get; private set; } = "";
}
