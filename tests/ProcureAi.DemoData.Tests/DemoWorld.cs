using ProcureAi.Access.Domain;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;
using ProcureAi.Suppliers.Domain;
using AccessSeed = ProcureAi.Access.Infrastructure.DemoSeeder;
using BudgetSeed = ProcureAi.Budgets.Infrastructure.DemoSeeder;
using SupplierSeed = ProcureAi.Suppliers.Infrastructure.DemoSeeder;

namespace ProcureAi.DemoData.Tests;

/// <summary>
/// What the services seed (Access: users, cost centers, departments, the default matrix; Suppliers: catalogs; Budgets: amounts), read from
/// the seeders themselves, so a scenario that names something the seed does not have fails here and not halfway through a seed run.
/// </summary>
public static class DemoWorld
{
    public static readonly IReadOnlyList<TenantUser> Users = [.. AccessSeed.Users()];
    public static readonly IReadOnlyList<CostCenter> CostCenters = [.. AccessSeed.CostCenters()];
    public static readonly IReadOnlyList<Department> Departments = [.. AccessSeed.Departments()];
    public static readonly IReadOnlyList<Supplier> Suppliers = [.. SupplierSeed.DemoSuppliers()];

    public static IReadOnlyList<Scenario> Scenarios { get; } = ProcureAi.DemoData.Scenarios.Load(Path.Combine(AppContext.BaseDirectory, "Scenarios"));

    public static Scenario Acme => Scenarios.Single(s => s.Tenant == "acme");

    public static Scenario Globex => Scenarios.Single(s => s.Tenant == "globex");

    public static string UserId(string username) =>
        DemoUsers.All.TryGetValue(username, out var id) ? id : throw new InvalidOperationException($"{username} is not a demo user.");

    public static TenantUser? User(string tenant, string username) =>
        DemoUsers.All.TryGetValue(username, out var id) ? Users.SingleOrDefault(u => u.TenantId == tenant && u.UserId == id) : null;

    public static Supplier? Supplier(string tenant, string code) => Suppliers.SingleOrDefault(s => s.TenantId == tenant && s.Code == code);

    public static CostCenter? CostCenter(string tenant, string code) => CostCenters.SingleOrDefault(c => c.TenantId == tenant && c.Code == code);

    /// <summary>The same rule as Access's effective access: assigned, managed, in a department they head, or every one for finance.</summary>
    public static bool ActsFor(TenantUser user, CostCenter costCenter) =>
        user.CostCenterCodes.Contains(costCenter.Code)
        || costCenter.ManagerUserId == user.UserId
        || Departments.Any(d => d.TenantId == costCenter.TenantId && d.Code == costCenter.DepartmentCode && d.HeadUserId == user.UserId)
        || user.IsFinanceApprover;

    /// <summary>The request's total incl. VAT at the catalog prices, as Requests computes it.</summary>
    public static Money TotalInclVat(string tenant, ScenarioRequest request)
    {
        var supplier = Supplier(tenant, request.Supplier) ?? throw new InvalidOperationException($"No supplier {request.Supplier} in {tenant}.");
        var excl = request.Lines.Aggregate(Money.Zero, (sum, line) => sum + (supplier.Find(line.Sku)!.UnitPriceExclVat * line.Quantity));
        return excl.InclVat();
    }

    /// <summary>The seeded budget of the cost center (Rand incl. VAT).</summary>
    public static decimal Budget(string tenant, string costCenter) =>
        BudgetSeed.DemoBudgets.Single(b => b.Tenant == tenant && b.CostCenter == costCenter).Amount;

    /// <summary>The approval chain Approvals would plan for the request, from the seeded organisation and the default matrix.</summary>
    public static ApprovalChain Chain(string tenant, ScenarioRequest request)
    {
        var org = Organisation(tenant, request.CostCenter);
        return ApprovalRouting.Resolve(TotalInclVat(tenant, request), UserId(request.Requester), org, Thresholds);
    }

    public static OrganisationSnapshot Organisation(string tenant, string costCenterCode)
    {
        var costCenter = CostCenter(tenant, costCenterCode) ?? throw new InvalidOperationException($"No cost center {costCenterCode} in {tenant}.");
        var department = Departments.SingleOrDefault(d => d.TenantId == tenant && d.Code == costCenter.DepartmentCode);
        var users = Users.Where(u => u.TenantId == tenant).ToList();
        Candidate? Candidate(string? userId) => users.SingleOrDefault(u => u.UserId == userId) is { } u ? ToCandidate(u) : null;
        return new OrganisationSnapshot(
            costCenter.Code, Candidate(costCenter.ManagerUserId), Candidate(department?.HeadUserId),
            [.. users.Where(u => u.IsFinanceApprover).Select(ToCandidate)], costCenter.DepartmentCode);
    }

    public static Candidate ToCandidate(TenantUser user) =>
        new(user.UserId, user.ApprovalLimit, user.Roles.Any(r => DefaultPermissionMatrix.Roles.TryGetValue(r, out var p) && p.Contains(Permissions.RequestsApprove)),
            user.DisplayName);

    /// <summary>Approvals' thresholds (its appsettings.json): a department head above R 20 000, finance above R 200 000, incl. VAT.</summary>
    public static readonly ApprovalThresholds Thresholds = new(Money.Zar(20_000m), Money.Zar(200_000m));
}
