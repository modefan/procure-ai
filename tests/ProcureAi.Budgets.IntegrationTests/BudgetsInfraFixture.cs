using Microsoft.EntityFrameworkCore;
using ProcureAi.Budgets.Domain;
using ProcureAi.Budgets.Infrastructure;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>The shared service infrastructure with the Budgets database migrated and demo-seeded by the real <c>--migrate</c> step.</summary>
public sealed class BudgetsInfraFixture()
    : ServiceInfraFixture("budgetsdb", "budgets", "budget_reservations", "budget_spends", "audit_entries")
{
    public const string ApiAssembly = "ProcureAi.Budgets.Api.dll";

    /// <summary>The fiscal year the demo seed and the tests treat as the current one (judged by the clock, in Johannesburg time).</summary>
    public static FiscalYear CurrentYear =>
        FiscalYear.For(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg"));

    public BudgetsDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<BudgetsDbContext>().UseNpgsql(DbConnectionString).Options);

    /// <summary>A budget of the current fiscal year for a tenant of the test's own, so tests never share or change the seeded data.</summary>
    public async Task<CostCenterBudget> CreateBudgetAsync(string tenant, string costCenter, decimal amount)
    {
        await using var db = NewDbContext();
        var budget = CostCenterBudget.Create(tenant, costCenter, CurrentYear, Money.Zar(amount));
        db.Budgets.Add(budget);
        await db.SaveChangesAsync();
        return budget;
    }

    public static string NewTenant() => $"t{Guid.NewGuid():N}"[..12];

    // The seed is written before Debezium starts, so it is not captured (seed data is not a user action).
    protected override Task MigrateAsync(string connectionString) =>
        ApiMigrateProcess.RunAsync(ApiAssembly, "budgetsdb", connectionString, RabbitConnectionString, RedisConnectionString, demoSeed: true);
}
