using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.Audit.Infrastructure;

/// <summary>The migration entry point. Nothing migrates at normal startup: the Api --migrate flag calls this and exits.</summary>
public static class AuditDatabase
{
    /// <summary>Creates or updates Marten's schema objects (the document tables and indexes) in <c>auditdb</c>.</summary>
    public static async Task MigrateAuditDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }
}
