using MongoDB.Driver;

namespace ProcureAi.BuildingBlocks.Persistence.Mongo;

/// <summary>
/// A step of a MongoDB service's <c>--migrate</c> run (indexes, collection options). Register with
/// <c>services.AddSingleton&lt;IMongoMigration, ...&gt;()</c>; the host runs every one, in registration order, before the demo
/// seed. MongoDB has no schema, so a migration must be idempotent (creating an index that exists is a no-op).
/// Migrations get the raw database: they run as the system, not for a tenant.
/// </summary>
public interface IMongoMigration
{
    Task ApplyAsync(IMongoDatabase database, CancellationToken cancellationToken);
}
