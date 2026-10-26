using Microsoft.AspNetCore.Hosting;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>
/// The real Budgets API (its Program) on a test server, against the shared Postgres, RabbitMQ and Redis, with the stub Access
/// service. Several factories on the same infrastructure stand in for several instances of the service.
/// </summary>
public sealed class BudgetsApiFactory(BudgetsInfraFixture infra, StubAccessServer access)
    : ServiceApiFactory<Program>("budgetsdb", infra.DbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in access.Settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
