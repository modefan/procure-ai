using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Audit.Infrastructure.PgAudit;

namespace ProcureAi.Audit.Infrastructure;

/// <summary>The migration entry point. Nothing migrates at normal startup: the Api --migrate flag calls this and exits.</summary>
public static class AuditDatabase
{
    /// <summary>
    /// Creates or updates Marten's schema objects (the document tables and indexes) in <c>auditdb</c>, and the audit log reader with
    /// its <c>file_fdw</c> table (<see cref="PgAuditLogSetup"/>; as the migration's own user, the server's superuser).
    /// </summary>
    public static async Task MigrateAuditDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        var configuration = services.GetRequiredService<IConfiguration>();
        var pgAudit = services.GetRequiredService<IOptions<PgAuditOptions>>().Value;
        var database = configuration.GetConnectionString(DependencyInjection.DatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string {DependencyInjection.DatabaseConnectionName} is not configured.");
        await PgAuditLogSetup.EnsureAsync(database, DependencyInjection.AuditLogConnectionString(configuration, pgAudit), cancellationToken);
    }
}
