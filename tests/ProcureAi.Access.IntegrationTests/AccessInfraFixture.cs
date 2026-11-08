using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Domain;
using ProcureAi.Access.Infrastructure;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>The shared service infrastructure with the Access database migrated and demo-seeded by the real <c>--migrate</c> step.</summary>
public sealed class AccessInfraFixture()
    : ServiceInfraFixture("accessdb", "permission_matrices", "permission_grants", "departments", "cost_centers", "tenant_users", "system_settings", "audit_entries")
{
    /// <summary>
    /// A new tenant with the default matrix and one user (Maria, manager, R 200 000), for a test that changes data. Tests never
    /// change the seeded tenants, which other tests read, so no test depends on the order of the others.
    /// </summary>
    public async Task<string> CreateTenantAsync(string? tenantId = null)
    {
        var tenant = tenantId ?? $"t{Guid.NewGuid():N}"[..12];
        await using var db = new AccessDbContext(new DbContextOptionsBuilder<AccessDbContext>().UseNpgsql(DbConnectionString).Options);
        db.PermissionMatrices.Add(DefaultPermissionMatrix.CreateFor(tenant, DateTimeOffset.UtcNow));
        db.TenantUsers.Add(TenantUser.Create(
            tenant, DemoUsers.Maria, "Maria Naidoo", "maria@" + tenant + ".test", ["manager"], Money.Zar(200_000m), false, []));
        await db.SaveChangesAsync();
        return tenant;
    }

    /// <summary>Another database on the same Postgres, migrated and demo-seeded like the shared one (for a test that must not touch it).</summary>
    public async Task<string> CreateSeededDatabaseAsync(string name)
    {
        var connectionString = await CreateEmptyDatabaseAsync(name);
        await MigrateAsync(connectionString);
        return connectionString;
    }

    // The seed is written before Debezium starts, so it is not captured (seed data is not a user action).
    protected override Task MigrateAsync(string connectionString) =>
        ApiMigrateProcess.RunAsync("ProcureAi.Access.Api.dll", "accessdb", connectionString, RabbitConnectionString, RedisConnectionString, demoSeed: true);
}

[CollectionDefinition(Name)]
public sealed class AccessInfraCollection : ICollectionFixture<AccessInfraFixture>, ICollectionFixture<LazyKeycloak>
{
    public const string Name = "access-infra";
}
