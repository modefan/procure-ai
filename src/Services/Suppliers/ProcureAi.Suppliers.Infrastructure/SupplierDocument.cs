using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using ProcureAi.Suppliers.Domain;

namespace ProcureAi.Suppliers.Infrastructure;

/// <summary>
/// How a supplier is stored. A class of its own, so the domain stays free of MongoDB (its ID, tenant marker and the BSON
/// representation of the price live here). The ID is <c>{tenantId}:{code}</c>, so a code is unique within a tenant and the same code
/// can exist in another.
/// </summary>
internal sealed class SupplierDocument : ITenantDocument
{
    [BsonId]
    public string Id { get; set; } = null!;

    public string TenantId { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string VatNumber { get; set; } = "";

    public string Email { get; set; } = "";

    public bool Active { get; set; }

    public List<CatalogItemDocument> Items { get; set; } = [];

    public static SupplierDocument From(Supplier supplier) => new()
    {
        Id = supplier.Id,
        TenantId = supplier.TenantId,
        Code = supplier.Code,
        Name = supplier.Name,
        VatNumber = supplier.VatNumber,
        Email = supplier.Email,
        Active = supplier.Active,
        Items = [.. supplier.Items.Select(CatalogItemDocument.From)],
    };

    public Supplier ToDomain() =>
        new(TenantId, Code, Name, VatNumber, Email, Active, Items.Select(i => i.ToDomain()));
}

/// <summary>A catalog line as stored. The price is a Decimal128 (exact decimals, so Rand and cents never go through a double).</summary>
internal sealed class CatalogItemDocument
{
    public string Sku { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string Unit { get; set; } = null!;

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal UnitPriceExclVat { get; set; }

    public bool Active { get; set; }

    public static CatalogItemDocument From(CatalogItem item) => new()
    {
        Sku = item.Sku,
        Description = item.Description,
        Unit = item.Unit,
        UnitPriceExclVat = item.UnitPriceExclVat.Amount,
        Active = item.Active,
    };

    // Prices are stored in Rand only (the domain refuses anything else), so no currency is stored.
    public CatalogItem ToDomain() => new(Sku, Description, Unit, Money.Zar(UnitPriceExclVat), Active);
}
