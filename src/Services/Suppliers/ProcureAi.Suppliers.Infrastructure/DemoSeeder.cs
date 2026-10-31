using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using ProcureAi.Suppliers.Domain;

namespace ProcureAi.Suppliers.Infrastructure;

/// <summary>
/// The demo catalogs the AppHost migrate step writes when DemoSeed:Enabled is true. Idempotent: each supplier is read and then
/// inserted, or replaced by its definition here when it differs, so a second run changes nothing (not even the audit stamp). The seed works through a
/// <see cref="TenantCollection{T}"/> for the tenant it seeds, so even the seed cannot write another tenant's documents.
/// </summary>
public static class DemoSeeder
{
    public static async Task SeedDemoDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var database = services.GetRequiredService<IMongoDatabase>();
        await SeedAsync(database.GetCollection<SupplierDocument>(DependencyInjection.SuppliersCollection), cancellationToken);
    }

    private static async Task SeedAsync(IMongoCollection<SupplierDocument> collection, CancellationToken cancellationToken)
    {
        foreach (var supplier in DemoSuppliers())
        {
            var tenantCollection = new TenantCollection<SupplierDocument>(collection, new SeedCaller(supplier.TenantId));
            var document = SupplierDocument.From(supplier);
            var existing = await tenantCollection.Find(d => d.Id == document.Id).FirstOrDefaultAsync(cancellationToken);
            if (existing is null)
            {
                await tenantCollection.InsertOneAsync(document, cancellationToken);
            }
            else if (!SameContent(existing, document))
            {
                await tenantCollection.ReplaceOneAsync(d => d.Id == document.Id, document, cancellationToken: cancellationToken);
            }
        }
    }

    // Unchanged documents are not written again: every write carries a new audit stamp (and is captured by change data capture),
    // so a re-run of the seed must leave them alone. The stamps themselves are not content.
    private static bool SameContent(SupplierDocument existing, SupplierDocument seeded)
    {
        var stamp = existing.Audit;
        existing.Audit = null;
        try
        {
            return existing.ToBsonDocument() == seeded.ToBsonDocument();
        }
        finally
        {
            existing.Audit = stamp;
        }
    }

    private static CatalogItem Item(string sku, string description, string unit, decimal price, bool active = true) =>
        new(sku, description, unit, Money.Zar(price), active);

    /// <summary>The seeded suppliers and their catalogs (also read by the demo data scenario tests).</summary>
    public static IEnumerable<Supplier> DemoSuppliers()
    {
        CatalogItem[] contosoItems =
        [
            Item("LAP-14", "Laptop 14\"", "each", 25_200m),
            Item("MON-27", "Monitor 27\"", "each", 4_200m),
            Item("DOCK-C", "USB-C dock", "each", 2_100m),
            Item("KB-WL", "Wireless keyboard", "each", 650m),
            // The supplier simulator rejects DISC- SKUs, so an order for this one fails the way a discontinued item does.
            Item("DISC-01", "Discontinued webcam", "each", 990m, active: false),
        ];

        yield return new Supplier("acme", "contoso-hardware", "Contoso Hardware", "4123456789", "orders@contoso-hardware.test", true, contosoItems);
        yield return new Supplier(
            "acme", "fabrikam-office", "Fabrikam Office", "4234567890", "sales@fabrikam-office.test", true,
            [
                Item("CHAIR-ERG", "Ergonomic chair", "each", 6_210m),
                Item("DESK-SS", "Sit-stand desk", "each", 9_900m),
                Item("PAPER-A4", "A4 paper, box of 5 reams", "box", 450m),
                // The demo's PO-1001 (plan 06 ruling 3): a dock and a stand, received in full and waiting for the scanned invoice.
                Item("DOCK-USB", "USB-C Dock", "each", 2_350m),
                Item("STAND-LP", "Laptop Stand", "each", 690m),
            ]);
        yield return new Supplier(
            "acme", "adatum-software", "Adatum Software", "4345678901", "licences@adatum-software.test", true,
            [Item("LIC-OFF", "Office suite licence, 1 year", "licence", 3_450m)]);
        yield return new Supplier(
            "globex", "northwind-traders", "Northwind Traders", "4456789012", "orders@northwind-traders.test", true,
            [
                Item("TONER-K", "Black toner", "each", 1_380m),
                Item("LAP-14", "Laptop 14\"", "each", 24_900m),
            ]);
        // The sandbox company of the public demo gets the same hardware supplier as acme.
        yield return new Supplier("demo", "contoso-hardware", "Contoso Hardware", "4123456789", "orders@contoso-hardware.test", true, contosoItems);
    }

    /// <summary>The seed acts for the tenant whose catalog it writes.</summary>
    private sealed class SeedCaller(string tenantId) : ICallerContext
    {
        public string UserId => SystemCaller.Id;

        public string ActorId => SystemCaller.Id;

        public string? TenantId => tenantId;

        public string? CallingService => null;

        public CallerKind Kind => CallerKind.System;
    }
}
