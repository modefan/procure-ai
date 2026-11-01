using System.Net;
using Microsoft.EntityFrameworkCore;
using ProcureAi.Budgets.Domain;
using ProcureAi.Budgets.Infrastructure;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>
/// The nightly demo reset of Budgets (plan 06 rulings 4 and 5) on a database of its own: every budget, reservation and spend is deleted
/// with ordinary deletes the interceptor records, and the demo budgets come back with their seeded amounts.
/// </summary>
[Collection(BudgetsCollection.Name)]
public sealed class DemoResetTests(BudgetsApp app)
{
    [Fact]
    public async Task Demo_reset_clears_business_rows_and_reseeds()
    {
        var database = await app.Infra.CreateSeededDatabaseAsync($"reset_{Guid.NewGuid():N}");
        await using var factory = new BudgetsApiFactory(
            app.Infra, app.Access, app.Faults, database, new Dictionary<string, string?> { ["DemoReset:Enabled"] = "true" });
        var tenant = BudgetsInfraFixture.NewTenant();
        Guid extra;
        await using (var db = NewDbContext(database))
        {
            var budget = CostCenterBudget.Create(tenant, "CC-1", BudgetsInfraFixture.CurrentYear, Money.Zar(10_000m));
            budget.Reserve(Guid.NewGuid(), Money.Zar(1_000m));
            db.Budgets.Add(budget);
            extra = budget.Id;
            var seeded = await db.Budgets.IgnoreQueryFilters().SingleAsync(
                b => b.TenantId == "acme" && b.CostCenterCode == "IT-200" && b.FiscalYear == BudgetsInfraFixture.CurrentYear, TestContext.Current.CancellationToken);
            seeded.SetAmount(Money.Zar(1m));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateClient();
        (await DemoResetProbe.ResetAsync(client, "budgets")).ShouldBe(HttpStatusCode.NoContent);

        await using (var db = NewDbContext(database))
        {
            var budgets = await db.Budgets.IgnoreQueryFilters().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
            budgets.ShouldNotContain(b => b.Id == extra);
            budgets.Select(b => (b.TenantId, b.CostCenterCode, b.FiscalYear.EndYear, b.Amount.Amount)).OrderBy(b => b.ToString()).ShouldBe(
                DemoSeeder.DemoBudgets
                    .SelectMany(d => new[] { BudgetsInfraFixture.CurrentYear.EndYear, BudgetsInfraFixture.CurrentYear.EndYear + 1 }
                        .Select(year => (d.Tenant, d.CostCenter, year, d.Amount)))
                    .OrderBy(b => b.ToString()));
        }

        (await DemoResetProbe.CountAsync(database, "select count(*) from budget_reservations")).ShouldBe(0);
        // Every row deleted with an interceptor row of its own (ruling 5): the extra budget and its reservation, and the seeded budgets.
        (await DemoResetProbe.DeleteEntriesAsync(database, "budgets")).Select(d => d.Key).ShouldContain(extra.ToString());
        (await DemoResetProbe.DeleteEntriesAsync(database, "budget_reservations")).Count.ShouldBe(1);

        (await DemoResetProbe.ResetAsync(client, "budgets")).ShouldBe(HttpStatusCode.NoContent, "twice is safe");
    }

    private static BudgetsDbContext NewDbContext(string database) =>
        new(new DbContextOptionsBuilder<BudgetsDbContext>().UseNpgsql(database).Options);

    /// <summary>
    /// The service's own <c>audit_entries</c> are demo audit data too (spec 5.1, plan 07 Task 0b): the reset empties them first, so afterwards
    /// the table holds only the reset's own Delete rows (ruling 5) and what came after.
    /// </summary>
    [Fact]
    public async Task Demo_reset_clears_audit_entries()
    {
        var database = await app.Infra.CreateSeededDatabaseAsync($"reset_{Guid.NewGuid():N}");
        await using var factory = new BudgetsApiFactory(
            app.Infra, app.Access, app.Faults, database, new Dictionary<string, string?> { ["DemoReset:Enabled"] = "true" });
        var earlier = await DemoResetProbe.InsertEarlierAuditEntryAsync(database);
        var started = DateTimeOffset.UtcNow;
        using var client = factory.CreateClient();

        (await DemoResetProbe.ResetAsync(client, "budgets")).ShouldBe(HttpStatusCode.NoContent);

        (await DemoResetProbe.CountAsync(database, $"select count(*) from audit_entries where id = '{earlier}'")).ShouldBe(0, "yesterday's interceptor row");
        (await DemoResetProbe.AuditEntriesBeforeAsync(database, started)).ShouldBe(0, "every audit_entries row from before the reset is gone (carry-over 5)");
        (await DemoResetProbe.DeleteEntriesAsync(database, "budgets")).ShouldNotBeEmpty("the reset's own Delete rows stay");
    }
}
