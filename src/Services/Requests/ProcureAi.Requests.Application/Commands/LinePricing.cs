using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Commands;

/// <summary>Turns the SKUs a requester picked into request lines with the description and price of the supplier's catalog.</summary>
internal static class LinePricing
{
    /// <summary>
    /// Looks the SKUs up (one call) and checks the supplier and every SKU: an unknown or inactive supplier, or an unknown or inactive
    /// SKU, is a business-rule error (422) naming what is wrong. Returns the catalog entry of every asked SKU. No SKUs, no call.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, CatalogPrice>> LookUpAsync(
        ISupplierCatalog catalog, string supplierCode, IReadOnlyCollection<string> skus, string tenantId, CancellationToken cancellationToken)
    {
        if (skus.Count == 0)
        {
            return new Dictionary<string, CatalogPrice>();
        }

        var supplier = await catalog.GetItemsAsync(supplierCode, skus, tenantId, cancellationToken)
            ?? throw new DomainException($"Unknown supplier '{supplierCode}'.");
        if (!supplier.Active)
        {
            throw new DomainException($"Supplier '{supplierCode}' is not active.");
        }

        var unknown = skus.Where(s => !supplier.Items.ContainsKey(s)).ToList();
        if (unknown.Count > 0)
        {
            throw new DomainException($"Unknown SKU for supplier '{supplierCode}': {string.Join(", ", unknown)}.");
        }

        var inactive = skus.Where(s => !supplier.Items[s].Active).ToList();
        if (inactive.Count > 0)
        {
            throw new DomainException($"SKU not active for supplier '{supplierCode}': {string.Join(", ", inactive)}.");
        }

        return supplier.Items;
    }

    public static void AddFromCatalog(PurchaseRequest request, LineInput line, CatalogPrice price) =>
        request.AddLine(line.Sku, price.Description, line.Quantity, Money.Zar(price.UnitPriceExclVat));
}
