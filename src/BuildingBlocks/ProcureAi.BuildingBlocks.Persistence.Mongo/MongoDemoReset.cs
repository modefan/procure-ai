using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Persistence.Mongo;

/// <summary>
/// The MongoDB half of a service's demo reset (plan 06 rulings 4 and 5): deletes every document of a collection, tenant by tenant, through
/// <see cref="TenantCollection{T}"/>, so each delete is preceded by its "deleting" audit stamp and Audit sees an app delete, never a change
/// made outside the app (a raw <c>deleteMany</c> or a dropped collection would be).
/// </summary>
public static partial class MongoDemoReset
{
    /// <summary>
    /// Deletes all documents of every tenant found in <paramref name="collection"/>. Returns how many were deleted. A document without a
    /// tenant cannot be deleted tenant by tenant: it is kept, and the number kept is logged as a warning (final review F14).
    /// </summary>
    public static async Task<long> DeleteAllAsync<T>(
        IMongoCollection<T> collection, ILogger logger, TimeProvider? clock = null, CancellationToken cancellationToken = default)
        where T : ITenantDocument
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(logger);
        var tenants = await collection.Find(FilterDefinition<T>.Empty)
            .Project(Builders<T>.Projection.Expression(d => d.TenantId))
            .ToListAsync(cancellationToken);
        long deleted = 0;
        foreach (var tenant in tenants.Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.Ordinal))
        {
            var scoped = new TenantCollection<T>(collection, new ResetCaller(tenant), clock);
            deleted += (await scoped.DeleteManyAsync(FilterDefinition<T>.Empty, cancellationToken)).DeletedCount;
        }

        var skipped = tenants.Count(string.IsNullOrEmpty);
        if (skipped > 0)
        {
            LogSkipped(logger, skipped, collection.CollectionNamespace.CollectionName);
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo reset skipped {Count} documents without a tenant in {Collection}")]
    private static partial void LogSkipped(ILogger logger, int count, string collection);

    /// <summary>The reset acts as the system, inside one tenant at a time (a <see cref="TenantCollection{T}"/> needs a tenant).</summary>
    private sealed class ResetCaller(string tenantId) : ICallerContext
    {
        public string UserId => SystemCaller.Id;

        public string ActorId => SystemCaller.Id;

        public string? TenantId => tenantId;

        public string? CallingService => null;

        public CallerKind Kind => CallerKind.System;
    }
}
