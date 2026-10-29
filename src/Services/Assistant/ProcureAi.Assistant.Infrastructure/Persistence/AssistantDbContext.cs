using Microsoft.EntityFrameworkCore;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence;

namespace ProcureAi.Assistant.Infrastructure.Persistence;

/// <summary>
/// The Assistant's Postgres data (<c>assistantdb</c>): policy documents, their file bytes, the chunks with their embeddings
/// (pgvector, <c>vector(1536)</c>, HNSW cosine index) and full-text vectors (GIN), the past purchases and the audit rows. Every query is
/// limited to the caller's tenant; without a caller (migrations, message handlers that carry the tenant) nothing matches, and those
/// handlers ignore the filter on purpose. Wolverine's outbox and inbox tables live in the same database (created by <c>--migrate</c>).
/// </summary>
public sealed class AssistantDbContext(DbContextOptions<AssistantDbContext> options, ICallerContext? callerContext = null)
    : DbContext(options), IAssistantDbContext, IAuditContextSource
{
    public const string VectorExtension = "vector";

    public string CurrentTenantId => Caller?.TenantId ?? "";

    public ICallerContext? Caller { get; } = callerContext;

    public DbSet<PolicyDocument> PolicyDocuments => Set<PolicyDocument>();

    public DbSet<PolicyDocumentFile> PolicyDocumentFiles => Set<PolicyDocumentFile>();

    public DbSet<Chunk> Chunks => Set<Chunk>();

    public DbSet<Purchase> Purchases => Set<Purchase>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // pgvector is in the custom Postgres image (infra/postgres); the first migration creates the extension in this database.
        modelBuilder.HasPostgresExtension(VectorExtension);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AssistantDbContext).Assembly);
        modelBuilder.Entity<PolicyDocument>().HasQueryFilter(d => d.TenantId == CurrentTenantId && d.TenantId != "");
        modelBuilder.Entity<PolicyDocumentFile>().HasQueryFilter(f => f.TenantId == CurrentTenantId && f.TenantId != "");
        modelBuilder.Entity<Chunk>().HasQueryFilter(c => c.TenantId == CurrentTenantId && c.TenantId != "");
        modelBuilder.Entity<Purchase>().HasQueryFilter(p => p.TenantId == CurrentTenantId && p.TenantId != "");
        // Written by AuditSaveChangesInterceptor, registered in DependencyInjection.
        modelBuilder.AddAuditEntries();
    }
}
