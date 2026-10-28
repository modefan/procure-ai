using Marten;
using JasperFx;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Domain;

namespace ProcureAi.Audit.Infrastructure;

/// <summary>
/// The business audit in Marten.
/// <para>
/// The write runs in a Marten session of its own, outside Wolverine's transaction (the recording handler takes no
/// <c>IDocumentSession</c>, so Wolverine wraps no transaction around it). That is safe because the write is idempotent: the durable
/// inbox already holds the message, and a handler that fails after the commit is run again and finds the record there.
/// </para>
/// <para>
/// Insert-only on purpose, idempotent by ID: an event is inserted, never updated or upserted. The first delivery of a message
/// wins and a redelivery (same message ID, so same document ID) is ignored, so a stored audit record can never be overwritten,
/// not even by a message that reuses an ID with other content. Do not turn this into <c>Store</c> (an upsert).
/// </para>
/// </summary>
public sealed class MartenAuditStore(IDocumentStore store) : IAuditStore
{
    public async Task AppendAsync(BusinessEvent businessEvent, CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession();
        session.Insert(businessEvent);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (DocumentAlreadyExistsException)
        {
            // Already recorded: a redelivery of the same message.
        }
    }

    public async Task<IReadOnlyList<BusinessEvent>> ForSubjectAsync(string tenantId, Guid subjectId, CancellationToken cancellationToken)
    {
        await using var session = store.QuerySession();
        return await session.Query<BusinessEvent>()
            .Where(e => e.TenantId == tenantId && e.SubjectId == subjectId)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BusinessEvent>> LatestAsync(string tenantId, int limit, CancellationToken cancellationToken)
    {
        await using var session = store.QuerySession();
        return await session.Query<BusinessEvent>()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BusinessEvent>> SubmissionsAsync(
        string tenantId, IReadOnlyCollection<Guid> subjectIds, CancellationToken cancellationToken)
    {
        if (subjectIds.Count == 0)
        {
            return [];
        }

        var ids = subjectIds.Select(id => (Guid?)id).ToArray();
        await using var session = store.QuerySession();
        return await session.Query<BusinessEvent>()
            .Where(e => e.TenantId == tenantId && e.Type == "RequestSubmitted" && ids.Contains(e.SubjectId))
            .ToListAsync(cancellationToken);
    }
}
