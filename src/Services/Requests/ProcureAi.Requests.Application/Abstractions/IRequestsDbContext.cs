using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Abstractions;

/// <summary>What the handlers need from persistence. Tenant filtering is applied by the implementation.</summary>
public interface IRequestsDbContext
{
    DbSet<PurchaseRequest> PurchaseRequests { get; }

    /// <summary>For infrastructure services that must run in the same transaction (the request number generator).</summary>
    DatabaseFacade Database { get; }

    /// <summary>Forces the request row itself to be written, so its version changes even if only its lines changed.</summary>
    void MarkModified(PurchaseRequest request);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
