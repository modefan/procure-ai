using Microsoft.EntityFrameworkCore;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;

namespace ProcureAi.Approvals.Application.Abstractions;

/// <summary>
/// What the handlers need from persistence. Steps are filtered to the caller's tenant by the implementation; the saga names
/// its tenant itself (a timeout has no caller) and ignores the filter. The saga state is persisted by Wolverine in the same
/// DbContext and transaction.
/// </summary>
public interface IApprovalsDbContext
{
    DbSet<ApprovalStep> Steps { get; }

    /// <summary>The saga state, for reads (Wolverine loads and saves it for the saga's handlers). Not filtered by tenant.</summary>
    DbSet<ApprovalSaga> Sagas { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
