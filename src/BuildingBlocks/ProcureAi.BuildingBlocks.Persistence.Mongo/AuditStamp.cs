using System.Diagnostics;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Persistence.Mongo;

/// <summary>
/// Who wrote a MongoDB document, stored in the document itself as <c>_audit: { userId, actorId, callingService, correlationId, at }</c>
/// (plan 06 ruling 16; the actor and calling service since final review I1, as the EF Core interceptor rows have them):
/// the MongoDB counterpart of the EF Core audit interceptor. MongoDB change events carry no transaction ID to match an audit row
/// against, so every write the app makes carries a fresh stamp; Debezium publishes it with the change, and Audit attributes a change
/// whose insert, replace or update includes a new <c>_audit.at</c> to that user. A change without one (mongosh, a script) is
/// "changed outside the app".
/// <para>
/// A delete event carries no document, so the app first stamps the document with <see cref="Deleting"/> set and then deletes it
/// (<see cref="TenantCollection{T}"/> does both); Audit attributes the delete to the user of that stamp.
/// </para>
/// </summary>
public sealed class AuditStamp
{
    /// <summary>The element the stamp is stored under.</summary>
    public const string ElementName = "_audit";

    /// <summary>Tag the correlation ID middleware puts on the request activity (the same as the EF Core interceptor reads).</summary>
    public const string CorrelationTag = "correlation.id";

    /// <summary>The person the write was for (<see cref="ICallerContext.UserId"/>; <c>system</c> for the seed and jobs).</summary>
    [BsonElement("userId")]
    public string UserId { get; set; } = null!;

    /// <summary>Who performed the write (<see cref="ICallerContext.ActorId"/>: the user, <c>claude-admin</c> or <c>service:&lt;name&gt;</c>).</summary>
    [BsonElement("actorId")]
    [BsonIgnoreIfNull]
    public string? ActorId { get; set; }

    /// <summary>The service that made the call or sent the message, when known (<see cref="ICallerContext.CallingService"/>).</summary>
    [BsonElement("callingService")]
    [BsonIgnoreIfNull]
    public string? CallingService { get; set; }

    /// <summary>The correlation ID of the request or message that caused the write, when there is one.</summary>
    [BsonElement("correlationId")]
    [BsonIgnoreIfNull]
    public string? CorrelationId { get; set; }

    /// <summary>When the app wrote it (UTC).</summary>
    [BsonElement("at")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime At { get; set; }

    /// <summary>True on the stamp written right before the app deletes the document.</summary>
    [BsonElement("deleting")]
    [BsonIgnoreIfDefault]
    public bool Deleting { get; set; }

    /// <summary>The stamp for an insert or a replace (set it on the document's <see cref="IAuditedDocument.Audit"/>).</summary>
    public static AuditStamp For(ICallerContext caller, string? correlationId, DateTimeOffset at, bool deleting = false)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return new AuditStamp
        {
            UserId = caller.UserId,
            ActorId = caller.ActorId,
            CallingService = caller.CallingService,
            CorrelationId = correlationId,
            At = at.UtcDateTime,
            Deleting = deleting,
        };
    }

    /// <summary>Adds the stamp to an update (<c>$set: { _audit: ... }</c>), for writes with update operators.</summary>
    public static UpdateDefinition<T> Apply<T>(
        UpdateDefinition<T> update, ICallerContext caller, string? correlationId, DateTimeOffset at, bool deleting = false)
    {
        ArgumentNullException.ThrowIfNull(update);
        return Builders<T>.Update.Combine(update, Builders<T>.Update.Set(ElementName, For(caller, correlationId, at, deleting)));
    }

    /// <summary>
    /// The correlation ID of the current request or message: the <see cref="CorrelationTag"/> of the current activity or one of its
    /// parents, else the trace ID (as the EF Core interceptor does).
    /// </summary>
    public static string? CurrentCorrelationId()
    {
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
        {
            if (activity.GetTagItem(CorrelationTag) is string tagged)
            {
                return tagged;
            }
        }

        return Activity.Current?.TraceId.ToString();
    }
}

/// <summary>
/// A document whose writes are audited through change data capture: <see cref="TenantCollection{T}"/> stamps every insert, replace
/// and delete of it. Map the property to <see cref="AuditStamp.ElementName"/> (<c>[BsonElement("_audit")]</c>, <c>[BsonIgnoreIfNull]</c>).
/// </summary>
public interface IAuditedDocument
{
    AuditStamp? Audit { get; set; }
}
