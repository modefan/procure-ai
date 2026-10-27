using ProcureAi.Audit.Domain;

namespace ProcureAi.Audit.Application.Abstractions;

/// <summary>Where the business audit lives (Marten in Infrastructure). Application never sees the document store.</summary>
public interface IAuditStore
{
    /// <summary>Stores the event under its <see cref="BusinessEvent.Id"/>. Storing the same ID again leaves one document (idempotent).</summary>
    Task AppendAsync(BusinessEvent businessEvent, CancellationToken cancellationToken);

    /// <summary>The events of one tenant about one purchase request, oldest first.</summary>
    Task<IReadOnlyList<BusinessEvent>> ForSubjectAsync(string tenantId, Guid subjectId, CancellationToken cancellationToken);
}
