using Marten;
using JasperFx;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Domain;

namespace ProcureAi.Audit.Infrastructure;

/// <summary>
/// The business audit in Marten. An event is inserted, never updated: the first delivery of a message wins and a
/// redelivery (same message ID, so same document ID) is ignored, so a stored record cannot be overwritten.
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
}
