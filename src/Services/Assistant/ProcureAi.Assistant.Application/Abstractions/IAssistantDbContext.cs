using Microsoft.EntityFrameworkCore;
using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Abstractions;

/// <summary>The Assistant's Postgres data as its handlers see it (the DbContext lives in Infrastructure; tenant filters apply).</summary>
public interface IAssistantDbContext
{
    DbSet<PolicyDocument> PolicyDocuments { get; }

    DbSet<PolicyDocumentFile> PolicyDocumentFiles { get; }

    DbSet<Chunk> Chunks { get; }

    DbSet<Purchase> Purchases { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
