using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using ProcureAi.BuildingBlocks.Web.Demo;

namespace ProcureAi.Suppliers.Infrastructure;

/// <summary>
/// The Suppliers part of the nightly demo reset (plan 06 ruling 4): every supplier of every tenant is deleted through
/// <see cref="TenantCollection{T}"/> (each delete stamped as the app's, so Audit raises no outside-the-app alert), then
/// <see cref="DemoSeeder"/> writes the demo catalogs again. No message store to clear (Suppliers has none).
/// </summary>
public sealed class SuppliersDemoResetter(IMongoDatabase database, IServiceProvider services, TimeProvider time, ILogger<SuppliersDemoResetter> logger)
    : IDemoResetter
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        var deleted = await MongoDemoReset.DeleteAllAsync(
            database.GetCollection<SupplierDocument>(DependencyInjection.SuppliersCollection), time, cancellationToken);
        await services.SeedDemoDataAsync(cancellationToken);
        logger.LogInformation("Demo reset deleted {Count} suppliers and seeded the catalogs again", deleted);
    }
}
