namespace ProcureAi.Access.IntegrationTests;

/// <summary>The shared service infrastructure with the Access database migrated and demo-seeded by the real <c>--migrate</c> step.</summary>
public sealed class AccessInfraFixture()
    : ServiceInfraFixture("accessdb", "permission_matrices", "permission_grants", "departments", "cost_centers", "tenant_users", "audit_entries")
{
    // The seed is written before Debezium starts, so it is not captured (seed data is not a user action).
    protected override Task MigrateAsync(string connectionString) =>
        MigrateProcess.RunAsync(connectionString, RabbitConnectionString, RedisConnectionString, demoSeed: true);
}

[CollectionDefinition(Name)]
public sealed class AccessInfraCollection : ICollectionFixture<AccessInfraFixture>
{
    public const string Name = "access-infra";
}
