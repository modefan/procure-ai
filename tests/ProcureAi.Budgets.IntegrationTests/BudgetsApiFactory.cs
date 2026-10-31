using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Budgets.Infrastructure;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>
/// The real Budgets API (its Program) on a test server, against the shared Postgres, RabbitMQ and Redis, with the stub Access
/// service. Several factories on the same infrastructure stand in for several instances of the service. Every save also goes
/// through <paramref name="faults"/>, which a test arms to make one save fail as a database blip does.
/// </summary>
/// <param name="database">Another database than the shared one (null: the shared one).</param>
/// <param name="settings">Settings of the test's own, applied last.</param>
public sealed class BudgetsApiFactory(
    BudgetsInfraFixture infra, StubAccessServer access, TransientDatabaseFault faults, string? database = null,
    IReadOnlyDictionary<string, string?>? settings = null)
    : ServiceApiFactory<Program>("budgetsdb", database ?? infra.DbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    /// <summary>Short waits for the scheduled retries of a transient database failure, so a test sees the retry within seconds.</summary>
    public const string TransientRetryInitialDelay = "00:00:00.200";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in access.Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting("Messaging:TransientDatabaseRetry:InitialDelay", TransientRetryInitialDelay);
        builder.UseSetting("Messaging:TransientDatabaseRetry:MaxDelay", "00:00:01");
        builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<BudgetsDbContext>(options => options.AddInterceptors(faults)));
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
    }
}
