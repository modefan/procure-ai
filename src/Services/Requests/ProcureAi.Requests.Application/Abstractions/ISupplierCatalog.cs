namespace ProcureAi.Requests.Application.Abstractions;

/// <summary>One catalog item as the Suppliers service lists it: the description and the price a request line takes over.</summary>
public sealed record CatalogPrice(string Description, decimal UnitPriceExclVat, bool Active);

/// <summary>A supplier of the tenant and the asked items it has (SKU to item; SKUs it does not have are absent).</summary>
public sealed record SupplierItems(string Code, string Name, bool Active, IReadOnlyDictionary<string, CatalogPrice> Items);

/// <summary>
/// The supplier catalog (the Suppliers service): request lines take their description and unit price from it, so a requester
/// never types a price. A call that fails or is refused (Suppliers down, open circuit, timeout) with nothing cached throws; the API
/// answers it with 503 and <c>Retry-After</c>.
/// </summary>
public interface ISupplierCatalog
{
    /// <param name="tenantId">The request's tenant (always the argument, never taken from the caller).</param>
    /// <returns>The supplier with the asked items that exist, or null when the tenant has no such supplier.</returns>
    Task<SupplierItems?> GetItemsAsync(
        string supplierCode, IReadOnlyCollection<string> skus, string tenantId, CancellationToken cancellationToken);
}
