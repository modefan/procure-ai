using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Suppliers.Domain;

/// <summary>
/// A supplier of one tenant with its catalog. The same supplier code can exist in several tenants, each with its own items and
/// prices, so the ID is the tenant and the code together.
/// </summary>
public sealed class Supplier
{
    public const int MaxCodeLength = 60;
    public const int MaxNameLength = 120;

    public Supplier(
        string tenantId, string code, string name, string vatNumber, string email, bool active, IEnumerable<CatalogItem> items)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new DomainException("A supplier belongs to a tenant.");
        }

        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength)
        {
            throw new DomainException($"The supplier code is required and at most {MaxCodeLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new DomainException($"The supplier name is required and at most {MaxNameLength} characters.");
        }

        var list = items.ToList();
        var duplicate = list.GroupBy(i => i.Sku, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new DomainException($"The catalog of {code} lists {duplicate.Key} twice.");
        }

        TenantId = tenantId;
        Code = code;
        Name = name;
        VatNumber = vatNumber;
        Email = email;
        Active = active;
        Items = list;
    }

    /// <summary><c>{tenantId}:{code}</c>.</summary>
    public string Id => IdFor(TenantId, Code);

    public string TenantId { get; }

    public string Code { get; }

    public string Name { get; }

    public string VatNumber { get; }

    public string Email { get; }

    public bool Active { get; }

    public IReadOnlyList<CatalogItem> Items { get; }

    public static string IdFor(string tenantId, string code) => $"{tenantId}:{code}";

    /// <summary>The item with exactly this SKU (case-sensitive: <c>LAP-14</c> is not <c>lap-14</c>), active or not.</summary>
    public CatalogItem? Find(string sku) => Items.FirstOrDefault(i => string.Equals(i.Sku, sku, StringComparison.Ordinal));
}
