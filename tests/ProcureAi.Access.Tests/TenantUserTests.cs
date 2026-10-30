using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Access.Tests;

public sealed class TenantUserTests
{
    private static TenantUser NewUser(decimal limit = 0m) =>
        TenantUser.Create("acme", "u-1", "Maria", "maria@acme.test", ["manager"], Money.Zar(limit), false, ["IT-200"]);

    [Fact]
    public void Negative_approval_limit_is_refused()
    {
        var user = NewUser(1000m);

        Should.Throw<DomainException>(() => user.SetApprovalLimit(Money.Zar(-0.01m)));
        user.ApprovalLimit.ShouldBe(Money.Zar(1000m));
        Should.Throw<DomainException>(() => NewUser(-1m));
    }

    [Fact]
    public void Approval_limit_can_be_zero_or_raised()
    {
        var user = NewUser(1000m);

        user.SetApprovalLimit(Money.Zero);
        user.ApprovalLimit.Amount.ShouldBe(0m);
        user.SetApprovalLimit(Money.Zar(250_000m));
        user.ApprovalLimit.Amount.ShouldBe(250_000m);
    }

    [Fact]
    public void Approval_limit_must_be_in_rand()
    {
        Should.Throw<DomainException>(() => NewUser().SetApprovalLimit(new Money(10m, "USD")));
    }

    [Fact]
    public void Each_permission_lists_the_roles_that_grant_it()
    {
        var matrix = PermissionMatrix.Create("acme", new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["manager"] = [Permissions.RequestsRead, Permissions.RequestsApprove],
            ["requester"] = [Permissions.RequestsRead],
            ["admin"] = [Permissions.AdminAuthorization],
        }, DateTimeOffset.UtcNow);

        var sources = AccessResolver.PermissionSources(matrix, ["requester", "manager", "ghost"]);

        sources[Permissions.RequestsRead].ShouldBe(["manager", "requester"]);
        sources[Permissions.RequestsApprove].ShouldBe(["manager"]);
        sources.ShouldNotContainKey(Permissions.AdminAuthorization);
        AccessResolver.PermissionSources(null, ["manager"]).ShouldBeEmpty();
    }

    [Fact]
    public void Cost_centers_are_replaced_without_duplicates_and_blank_codes_refused()
    {
        var user = NewUser();

        user.SetCostCenters(["MK-110", "MK-110", "IT-200"]);
        user.CostCenterCodes.ShouldBe(["MK-110", "IT-200"]);
        Should.Throw<DomainException>(() => user.SetCostCenters([" "]));
        user.CostCenterCodes.ShouldBe(["MK-110", "IT-200"]);
    }

    [Fact]
    public void Roles_and_cost_centers_are_kept_without_duplicates()
    {
        var user = TenantUser.Create("acme", "u-1", "Maria", "m@acme.test", ["manager", "manager"], Money.Zero, false, ["IT-200", "IT-200", "MK-110"]);

        user.Roles.ShouldBe(["manager"]);
        user.CostCenterCodes.ShouldBe(["IT-200", "MK-110"]);
    }
}
