namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>The shared service infrastructure with the Approvals database migrated by the real <c>--migrate</c> step.</summary>
public sealed class ApprovalsInfraFixture() : ServiceInfraFixture("approvalsdb", "approval_steps", "audit_entries")
{
    public const string ApiAssembly = "ProcureAi.Approvals.Api.dll";

    /// <summary>Another, freshly migrated Approvals database on the same Postgres (for an instance that must not share state).</summary>
    public async Task<string> CreateMigratedDatabaseAsync(string name)
    {
        var connectionString = await CreateEmptyDatabaseAsync(name);
        await MigrateAsync(connectionString);
        return connectionString;
    }

    public Task MigrateAsync(string connectionString, string rabbit) =>
        ApiMigrateProcess.RunAsync(ApiAssembly, "approvalsdb", connectionString, rabbit, RedisConnectionString, demoSeed: false);

    protected override Task MigrateAsync(string connectionString) => MigrateAsync(connectionString, RabbitConnectionString);
}
