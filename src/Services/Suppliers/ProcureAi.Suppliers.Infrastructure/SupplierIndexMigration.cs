using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Persistence.Mongo;

namespace ProcureAi.Suppliers.Infrastructure;

/// <summary>The unique index <c>(TenantId, Code)</c>: a supplier code is unique within a tenant. Creating an index that exists is a no-op.</summary>
internal sealed class SupplierIndexMigration : IMongoMigration
{
    public const string IndexName = "tenant_code";

    public async Task ApplyAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var suppliers = database.GetCollection<SupplierDocument>(DependencyInjection.SuppliersCollection);
        var keys = Builders<SupplierDocument>.IndexKeys.Ascending(s => s.TenantId).Ascending(s => s.Code);
        await suppliers.Indexes.CreateOneAsync(
            new CreateIndexModel<SupplierDocument>(keys, new CreateIndexOptions { Unique = true, Name = IndexName }),
            cancellationToken: cancellationToken);
    }
}
