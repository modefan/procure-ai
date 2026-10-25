using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Access.Tests;

public sealed class EffectiveAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
    private const string Tenant = "acme";
    private const string Maria = "u-maria";
    private const string Dept = "u-dept";
    private const string Thandi = "u-thandi";

    private static readonly PermissionMatrix Matrix = DefaultPermissionMatrix.CreateFor(Tenant, Now);

    private static readonly Department[] Departments =
    [
        Department.Create(Tenant, "IT", "Information Technology", Dept),
        Department.Create(Tenant, "MK", "Marketing", "u-other"),
    ];

    private static readonly CostCenter[] CostCenters =
    [
        CostCenter.Create(Tenant, "IT-200", "IT Operations", "IT", Maria),
        CostCenter.Create(Tenant, "MK-110", "Brand", "MK"),
        CostCenter.Create(Tenant, "FN-300", "Finance", "FN"),
    ];

    private static TenantUser User(string id, string role, decimal limit, bool finance = false, params string[] assigned) =>
        TenantUser.Create(Tenant, id, id, $"{id}@acme.test", [role], Money.Zar(limit), finance, assigned);

    [Fact]
    public void Manager_of_IT_200_sees_IT_200()
    {
        var maria = User(Maria, "manager", 200_000m);

        var access = AccessResolver.Resolve(Matrix, maria, Maria, ["manager"], Departments, CostCenters);

        access.CostCenterCodes.ShouldBe(["IT-200"]);
        access.Permissions.ShouldContain(Permissions.RequestsApprove);
        access.ApprovalLimit.ShouldBe(Money.Zar(200_000m));
    }

    [Fact]
    public void Department_head_of_IT_sees_the_cost_centers_of_the_department()
    {
        var dept = User(Dept, "department-head", 500_000m);

        var access = AccessResolver.Resolve(Matrix, dept, Dept, ["department-head"], Departments, CostCenters);

        access.CostCenterCodes.ShouldBe(["IT-200"]);
    }

    [Fact]
    public void Finance_approver_sees_all_cost_centers()
    {
        var thandi = User(Thandi, "finance", 2_000_000m, finance: true);

        var access = AccessResolver.Resolve(Matrix, thandi, Thandi, ["finance"], Departments, CostCenters);

        access.CostCenterCodes.ShouldBe(["FN-300", "IT-200", "MK-110"]);
    }

    [Fact]
    public void Assigned_cost_centers_are_included_once()
    {
        var tom = User("u-tom", "requester", 0m, finance: false, "IT-200", "MK-110");

        var access = AccessResolver.Resolve(Matrix, tom, "u-tom", ["requester"], Departments, CostCenters);

        access.CostCenterCodes.ShouldBe(["IT-200", "MK-110"]);
    }

    [Fact]
    public void Roles_that_are_not_in_the_matrix_give_nothing()
    {
        var maria = User(Maria, "manager", 200_000m);

        var access = AccessResolver.Resolve(Matrix, maria, Maria, ["ghost"], Departments, CostCenters);

        access.Permissions.ShouldBeEmpty();
    }

    [Fact]
    public void Unknown_user_has_no_cost_centers_and_a_zero_limit()
    {
        var access = AccessResolver.Resolve(Matrix, null, "stranger", ["requester"], Departments, CostCenters);

        access.CostCenterCodes.ShouldBeEmpty();
        access.ApprovalLimit.ShouldBe(Money.Zero);
        access.Permissions.ShouldContain(Permissions.RequestsCreate);
    }

    [Fact]
    public void A_user_is_an_eligible_approver_only_when_their_stored_roles_grant_approve()
    {
        AccessResolver.CanApprove(Matrix, User(Maria, "manager", 1m)).ShouldBeTrue();
        AccessResolver.CanApprove(Matrix, User("u-tom", "requester", 0m)).ShouldBeFalse();
        AccessResolver.CanApprove(null, User(Maria, "manager", 1m)).ShouldBeFalse();
    }
}
