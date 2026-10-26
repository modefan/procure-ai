using Microsoft.EntityFrameworkCore;
using ProcureAi.Approvals.Application.Abstractions;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence;

namespace ProcureAi.Approvals.Infrastructure;

public sealed class ApprovalsDbContext(DbContextOptions<ApprovalsDbContext> options, ICallerContext? callerContext = null)
    : DbContext(options), IApprovalsDbContext, IAuditContextSource
{
    /// <summary>The tenant step queries are limited to. Without a caller (migrations, timeouts) nothing matches.</summary>
    public string CurrentTenantId => Caller?.TenantId ?? "";

    /// <summary>The caller of this context scope, for the audit interceptor.</summary>
    public ICallerContext? Caller { get; } = callerContext;

    public DbSet<ApprovalStep> Steps => Set<ApprovalStep>();

    /// <summary>The saga state, loaded and saved by Wolverine (its ID is the request's; no tenant filter, the saga names its tenant).</summary>
    public DbSet<ApprovalSaga> Sagas => Set<ApprovalSaga>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApprovalsDbContext).Assembly);
        modelBuilder.Entity<ApprovalStep>().HasQueryFilter(s => s.TenantId == CurrentTenantId);
        // Written by AuditSaveChangesInterceptor, registered in DependencyInjection.
        modelBuilder.AddAuditEntries();
    }
}
