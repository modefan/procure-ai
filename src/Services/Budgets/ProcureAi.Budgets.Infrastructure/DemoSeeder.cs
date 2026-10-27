using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Budgets.Application;
using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Budgets.Infrastructure;

/// <summary>
/// The demo data the AppHost migrate step writes when DemoSeed:Enabled is true: the budgets of the demo cost centers for the
/// current fiscal year and the next. Idempotent: a budget is added only when missing, so a second run changes nothing and
/// never overwrites an amount that was edited.
/// </summary>
public static class DemoSeeder
{
    public static async Task SeedDemoDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BudgetsDbContext>();
        var zone = scope.ServiceProvider.GetRequiredService<IOptions<BudgetsOptions>>().Value.Zone;
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await SeedAsync(db, FiscalYear.For(time.GetUtcNow(), zone), cancellationToken);
    }

    public static async Task SeedAsync(BudgetsDbContext db, FiscalYear current, CancellationToken cancellationToken)
    {
        // The seed has no caller, so the tenant filter matches nothing: read everything and add only what is missing.
        var existing = await db.Budgets.IgnoreQueryFilters()
            .Select(b => new { b.TenantId, b.CostCenterCode, b.FiscalYear })
            .ToListAsync(cancellationToken);
        var known = existing.Select(b => (b.TenantId, b.CostCenterCode, b.FiscalYear)).ToHashSet();

        FiscalYear[] years = [current, new FiscalYear(current.EndYear + 1)];
        foreach (var (tenant, costCenter, amount) in DemoBudgets)
        {
            foreach (var year in years.Where(y => !known.Contains((tenant, costCenter, y))))
            {
                db.Budgets.Add(CostCenterBudget.Create(tenant, costCenter, year, Money.Zar(amount)));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static readonly (string Tenant, string CostCenter, decimal Amount)[] DemoBudgets =
    [
        ("acme", "IT-200", 900_000m),
        ("acme", "MK-110", 360_000m),
        ("globex", "GX-100", 500_000m),
        // The sandbox of the demo company, shared by every self-registered user of the public demo.
        ("demo", "DEMO-100", 250_000m),
    ];
}
