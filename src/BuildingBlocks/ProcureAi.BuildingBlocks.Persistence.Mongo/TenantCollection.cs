using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Persistence.Mongo;

/// <summary>
/// A MongoDB collection of tenant documents that can only be reached for the caller's tenant: the MongoDB counterpart of the
/// EF Core tenant query filter. The raw <see cref="IMongoCollection{T}"/> is not exposed, so a service cannot read or write
/// another tenant's documents by accident:
/// <list type="bullet">
/// <item>every filter is <b>combined</b> with <c>TenantId == caller's tenant</c> (AND), never replaced, so an OR or a filter on
/// another tenant's ID still stays inside the caller's tenant;</item>
/// <item>an insert or replace with a document of another tenant throws <see cref="ForbiddenException"/> before anything is written;</item>
/// <item>a caller without a tenant (a system or job context) throws <see cref="ForbiddenException"/> on every operation.</item>
/// </list>
/// Operations that could move a document to another tenant (updates with operators, aggregation) are deliberately not offered:
/// read, change in code, then <see cref="ReplaceOneAsync(FilterDefinition{T}, T, ReplaceOptions?, CancellationToken)"/>.
/// Seeding and migrations (<see cref="IMongoMigration"/>) work on the raw database as the system, outside this wrapper.
/// <para>
/// A document that is <see cref="IAuditedDocument"/> is stamped on every write (plan 06 ruling 16): inserts and replaces carry the caller's
/// <see cref="AuditStamp"/>, and a delete first stamps the documents it is about to delete as "deleting", so change data capture can tell
/// the app's writes from changes made outside it.
/// </para>
/// </summary>
public sealed class TenantCollection<T>(IMongoCollection<T> collection, ICallerContext caller, TimeProvider? clock = null)
    where T : ITenantDocument
{
    private static readonly bool Audited = typeof(IAuditedDocument).IsAssignableFrom(typeof(T));

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // The element name of TenantId, found once from the class map (a document may rename it), and checked to be a plain string.
    private static readonly string TenantElement = ResolveTenantElement();

    public IFindFluent<T, T> Find(FilterDefinition<T> filter) => collection.Find(Scoped(filter, out _));

    public IFindFluent<T, T> Find(Expression<Func<T, bool>> filter) => Find((FilterDefinition<T>)filter);

    public Task<long> CountAsync(FilterDefinition<T> filter, CancellationToken cancellationToken = default) =>
        collection.CountDocumentsAsync(Scoped(filter, out _), cancellationToken: cancellationToken);

    public Task<long> CountAsync(Expression<Func<T, bool>> filter, CancellationToken cancellationToken = default) =>
        CountAsync((FilterDefinition<T>)filter, cancellationToken);

    public Task InsertOneAsync(T document, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        EnsureOwn(document, tenant);
        Stamp(document);
        return collection.InsertOneAsync(document, cancellationToken: cancellationToken);
    }

    public Task InsertManyAsync(IEnumerable<T> documents, CancellationToken cancellationToken = default)
    {
        var tenant = RequireTenant();
        var all = documents.ToList();
        foreach (var document in all)
        {
            EnsureOwn(document, tenant);
            Stamp(document);
        }

        return collection.InsertManyAsync(all, cancellationToken: cancellationToken);
    }

    /// <summary>Replaces the one document matching <paramref name="filter"/> in the caller's tenant; matches nothing for another tenant's.</summary>
    public Task<ReplaceOneResult> ReplaceOneAsync(
        FilterDefinition<T> filter, T replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default)
    {
        var scoped = Scoped(filter, out var tenant);
        EnsureOwn(replacement, tenant);
        Stamp(replacement);
        return collection.ReplaceOneAsync(scoped, replacement, options ?? new ReplaceOptions(), cancellationToken);
    }

    public Task<ReplaceOneResult> ReplaceOneAsync(
        Expression<Func<T, bool>> filter, T replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default) =>
        ReplaceOneAsync((FilterDefinition<T>)filter, replacement, options, cancellationToken);

    public async Task<DeleteResult> DeleteOneAsync(FilterDefinition<T> filter, CancellationToken cancellationToken = default)
    {
        var scoped = Scoped(filter, out _);
        if (Audited)
        {
            await collection.UpdateOneAsync(scoped, DeletingStamp(), cancellationToken: cancellationToken);
        }

        return await collection.DeleteOneAsync(scoped, cancellationToken);
    }

    public Task<DeleteResult> DeleteOneAsync(Expression<Func<T, bool>> filter, CancellationToken cancellationToken = default) =>
        DeleteOneAsync((FilterDefinition<T>)filter, cancellationToken);

    public async Task<DeleteResult> DeleteManyAsync(FilterDefinition<T> filter, CancellationToken cancellationToken = default)
    {
        var scoped = Scoped(filter, out _);
        if (Audited)
        {
            await collection.UpdateManyAsync(scoped, DeletingStamp(), cancellationToken: cancellationToken);
        }

        return await collection.DeleteManyAsync(scoped, cancellationToken);
    }

    private void Stamp(T document)
    {
        if (document is IAuditedDocument audited)
        {
            audited.Audit = AuditStamp.For(caller, AuditStamp.CurrentCorrelationId(), _clock.GetUtcNow());
        }
    }

    // A delete event carries no document: the stamp written right before it tells Audit the delete is the app's.
    private UpdateDefinition<T> DeletingStamp() =>
        Builders<T>.Update.Set(AuditStamp.ElementName, AuditStamp.For(caller, AuditStamp.CurrentCorrelationId(), _clock.GetUtcNow(), deleting: true));

    private FilterDefinition<T> Scoped(FilterDefinition<T> filter, out string tenant)
    {
        tenant = RequireTenant();
        var own = new BsonDocumentFilterDefinition<T>(new BsonDocument(TenantElement, tenant));
        return Builders<T>.Filter.And(own, filter);
    }

    private string RequireTenant() =>
        string.IsNullOrWhiteSpace(caller.TenantId)
            ? throw new ForbiddenException("This operation needs a tenant, and the caller has none.")
            : caller.TenantId;

    private static void EnsureOwn(T document, string tenant)
    {
        if (!string.Equals(document.TenantId, tenant, StringComparison.Ordinal))
        {
            throw new ForbiddenException("A document of another tenant cannot be written.");
        }
    }

    private static string ResolveTenantElement()
    {
        var member = BsonClassMap.LookupClassMap(typeof(T)).GetMemberMap(nameof(ITenantDocument.TenantId))
            ?? throw new InvalidOperationException($"{typeof(T).Name}.{nameof(ITenantDocument.TenantId)} is not a mapped member, so the tenant filter cannot be built.");
        if (member.GetSerializer() is not StringSerializer { Representation: BsonType.String })
        {
            throw new InvalidOperationException($"{typeof(T).Name}.{nameof(ITenantDocument.TenantId)} must be stored as a plain string.");
        }

        return member.ElementName;
    }
}
