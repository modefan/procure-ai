using Microsoft.EntityFrameworkCore;
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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
