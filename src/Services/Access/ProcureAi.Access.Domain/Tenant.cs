using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Access.Domain;

/// <summary>
/// A tenant (company) of the system: the ID the tokens carry (the Keycloak organization alias, e.g. <c>acme</c>) and the name it is
/// shown under (<c>Acme Corp</c>). Not tenant-scoped itself: it is the list of tenants, read by the screens and by the lookup of a
/// user's memberships.
/// </summary>
public sealed class Tenant
{
    private Tenant()
    {
    }

    public string Id { get; private set; } = "";

    public string Name { get; private set; } = "";

    public static Tenant Create(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new DomainException("Tenant ID is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Tenant name is required.");
        }

        return new Tenant { Id = id.Trim(), Name = name.Trim() };
    }
}
