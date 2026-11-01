using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;

namespace ProcureAi.Access.Infrastructure;

/// <summary>
/// The demo data the AppHost migrate step writes when DemoSeed:Enabled is true: three tenants, each with the default
/// matrix of spec 4.2, the organisation of acme and globex (Thandi in both), and the sandbox cost center <c>DEMO-100</c> of the demo company,
/// which every self-registered user gets (DemoRegistration:SandboxCostCenter), managed by the demo approver <c>approver@demo.test</c>. Idempotent: every row is added only when missing, so a
/// second run changes nothing and never overwrites a matrix or an approval limit an admin changed.
/// </summary>
public static class DemoSeeder
{
    public static readonly string[] Tenants = ["acme", "globex", "demo"];

    /// <summary>The names the tenants are shown under (plan 06 ruling 18).</summary>
    public static readonly IReadOnlyDictionary<string, string> TenantNames = new Dictionary<string, string>
    {
        ["acme"] = "Acme Corp",
        ["globex"] = "Globex",
        ["demo"] = "Demo company",
    };

    public static async Task SeedDemoDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
        await SeedAsync(db, cancellationToken);
    }

    public static async Task SeedAsync(AccessDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        // The seed has no caller, so the tenant filters match nothing: read everything and add only what is missing.
        var tenants = await db.Tenants.Select(t => t.Id).ToListAsync(cancellationToken);
        db.Tenants.AddRange(TenantNames.Where(t => !tenants.Contains(t.Key)).Select(t => Tenant.Create(t.Key, t.Value)));

        var matrices = await db.PermissionMatrices.IgnoreQueryFilters().Select(m => m.TenantId).ToListAsync(cancellationToken);
        db.PermissionMatrices.AddRange(Tenants.Except(matrices).Select(t => DefaultPermissionMatrix.CreateFor(t, now)));

        var departments = await db.Departments.IgnoreQueryFilters().Select(d => d.TenantId + "/" + d.Code).ToListAsync(cancellationToken);
        db.Departments.AddRange(Departments().Where(d => !departments.Contains(d.TenantId + "/" + d.Code)));

        var costCenters = await db.CostCenters.IgnoreQueryFilters().Select(c => c.TenantId + "/" + c.Code).ToListAsync(cancellationToken);
        db.CostCenters.AddRange(CostCenters().Where(c => !costCenters.Contains(c.TenantId + "/" + c.Code)));

        var users = await db.TenantUsers.IgnoreQueryFilters().Select(u => u.TenantId + "/" + u.UserId).ToListAsync(cancellationToken);
        db.TenantUsers.AddRange(Users().Where(u => !users.Contains(u.TenantId + "/" + u.UserId)));

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The seeded departments (also read by the demo data scenario tests).</summary>
    public static IEnumerable<Department> Departments() =>
    [
        Department.Create("acme", "IT", "Information Technology", DemoUsers.Dept),
        Department.Create("acme", "MK", "Marketing", DemoUsers.Dept),
        Department.Create("globex", "OPS", "Operations"),
        Department.Create("demo", "SANDBOX", "Demo Sandbox"),
    ];

    /// <summary>The seeded cost centers (also read by the demo data scenario tests).</summary>
    public static IEnumerable<CostCenter> CostCenters() =>
    [
        CostCenter.Create("acme", "IT-200", "IT Operations", "IT", DemoUsers.Maria),
        CostCenter.Create("acme", "MK-110", "Brand and Campaigns", "MK", DemoUsers.Maria),
        CostCenter.Create("globex", "GX-100", "Plant Operations", "OPS", DemoUsers.Ben),
        CostCenter.Create("demo", "DEMO-100", "Demo Sandbox", "SANDBOX", DemoUsers.DemoApprover),
    ];

    /// <summary>The seeded tenant users (also read by the demo data scenario tests).</summary>
    public static IEnumerable<TenantUser> Users() =>
    [
        TenantUser.Create("acme", DemoUsers.Tom, "Tom Mokoena", "tom@acme.test", ["requester"], Money.Zero, false, ["IT-200"]),
        TenantUser.Create("acme", DemoUsers.Maria, "Maria Naidoo", "maria@acme.test", ["manager"], Money.Zar(200_000m), false, []),
        TenantUser.Create("acme", DemoUsers.Dept, "Dept Head", "dept@acme.test", ["department-head"], Money.Zar(500_000m), false, []),
        TenantUser.Create("acme", DemoUsers.Thandi, "Thandi Dlamini", "thandi@acme.test", ["finance"], Money.Zar(2_000_000m), true, []),
        TenantUser.Create("acme", DemoUsers.Admin, "Admin User", "admin@acme.test", ["admin"], Money.Zero, false, []),
        TenantUser.Create("globex", DemoUsers.Ben, "Ben Smit", "ben@globex.test", ["manager"], Money.Zar(100_000m), false, []),
        // Thandi works for both companies (the tenant switch, plan 06 Task 14): finance in Globex too, same limit.
        TenantUser.Create("globex", DemoUsers.Thandi, "Thandi Dlamini", "thandi@acme.test", ["finance"], Money.Zar(2_000_000m), true, []),
        // The only approver of the demo company: manager of the sandbox, and a finance approver, so that one person covers every
        // level for any sandbox request (the chain merges into a single step). Without one, a self-registered user's submit
        // is refused with "Nobody is eligible to approve".
        TenantUser.Create("demo", DemoUsers.DemoApprover, "Demo Approver", "approver@demo.test", ["manager"], Money.Zar(5_000_000m), true, []),
    ];
}
