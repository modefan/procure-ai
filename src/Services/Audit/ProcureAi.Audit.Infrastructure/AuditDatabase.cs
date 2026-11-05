using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.Audit.Infrastructure;

/// <summary>The migration entry point. Nothing migrates at normal startup: the Api --migrate flag calls this and exits.</summary>
public static class AuditDatabase
{
    /// <summary>
    /// Creates or updates Marten's schema objects (the document tables and indexes) in <c>auditdb</c>, as Audit's own role <c>audit_app</c>
    /// (the database's owner). The audit log reader and its <c>file_fdw</c> tables come from infra/postgres/roles.sql (the superuser's part,
    /// run before every migration; see <see cref="PgAudit.PgAuditLogSetup"/>).
    /// </summary>
    public static async Task MigrateAuditDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }
}
