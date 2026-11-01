using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureAi.Access.Application.Commands;
using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Web.Demo;
using ProcureAi.Contracts.Access;
using Wolverine;
using Wolverine.Runtime;

namespace ProcureAi.Access.Infrastructure;

/// <summary>
/// The Access part of the nightly demo reset (plan 06 ruling 4): Wolverine's envelopes and the service's own <c>audit_entries</c>, then the matrices, departments, cost centers and
/// users of every tenant are deleted and <see cref="DemoSeeder"/> writes them again, in one transaction, so no caller ever sees a tenant
/// without its matrix. Kept: the tenants themselves, and the users who registered themselves into the Demo company (their Keycloak
/// accounts stay too; Keycloak is not touched). Afterwards one <c>AccessChanged(Matrix)</c> per tenant evicts every service's cached Access
/// answers, also those cached while the reset ran.
/// </summary>
public sealed class AccessDemoResetter(
    AccessDbContext db, IMessageBus bus, TimeProvider time, ILogger<AccessDemoResetter> logger) : IDemoResetter
{
    public const string DemoTenant = "demo";

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(WolverineDemoReset.ClearEnvelopesSql, cancellationToken);
        // Yesterday's interceptor rows go first (carry-over 5); the deletes below write the reset's own.
        await EfDemoReset.ClearAuditEntriesAsync(db, cancellationToken);

        var seededDemoUsers = DemoSeeder.Users().Where(u => u.TenantId == DemoTenant).Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var deleted = await EfDemoReset.DeleteAllRowsAsync(
                db, entity => entity is Tenant || entity is TenantUser { TenantId: DemoTenant } user && !seededDemoUsers.Contains(user.UserId),
                cancellationToken);
            await DemoSeeder.SeedAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Demo reset deleted {Count} rows and seeded Access again", deleted);
        }

        foreach (var tenant in DemoSeeder.Tenants)
        {
            await bus.PublishAsync(new AccessChanged(tenant, SetRolePermissionsHandler.MatrixKind, null, time.GetUtcNow()));
        }
    }
}
