using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Access.Tests;

public sealed class PermissionMatrixTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    private static PermissionMatrix Default() => DefaultPermissionMatrix.CreateFor("acme", Now);

    [Fact]
    public void Permissions_for_several_roles_are_the_union()
    {
        var matrix = Default();

        var permissions = matrix.PermissionsFor(["requester", "finance"]);

        permissions.ShouldContain(Permissions.RequestsCreate);
        permissions.ShouldContain(Permissions.InvoicesReleasePayment);
        permissions.ShouldNotContain(Permissions.AdminSystem);
        permissions.ShouldBe(matrix.PermissionsOf("requester").Union(matrix.PermissionsOf("finance")), ignoreOrder: true);
    }

    [Fact]
    public void Role_that_is_not_in_the_matrix_gives_nothing()
    {
        Default().PermissionsFor(["intern"]).ShouldBeEmpty();
    }

    [Fact]
    public void Unknown_permission_is_refused()
    {
        var matrix = Default();

        var error = Should.Throw<DomainException>(() => matrix.SetRolePermissions("manager", [Permissions.RequestsRead, "requests.teleport"], Now));

        error.Message.ShouldContain("requests.teleport");
        matrix.PermissionsOf("manager").ShouldContain(Permissions.RequestsApprove, "a refused change leaves the matrix as it was");
    }

    [Fact]
    public void Removing_admin_authorization_from_admin_is_refused()
    {
        var matrix = Default();

        var error = Should.Throw<DomainException>(() => matrix.SetRolePermissions("admin", [Permissions.AdminUsers], Now));

        error.Message.ShouldContain("lock admins out");
        matrix.PermissionsOf("admin").ShouldContain(Permissions.AdminAuthorization);
    }

    [Fact]
    public void Other_roles_may_lose_admin_authorization_and_admin_may_change_the_rest()
    {
        var matrix = Default();

        matrix.SetRolePermissions("admin", [Permissions.AdminAuthorization], Now);
        matrix.SetRolePermissions("manager", [Permissions.RequestsRead], Now);

        matrix.PermissionsOf("admin").ShouldBe([Permissions.AdminAuthorization]);
        matrix.PermissionsOf("manager").ShouldBe([Permissions.RequestsRead]);
    }

    [Fact]
    public void Setting_a_role_adds_and_removes_only_the_difference()
    {
        var matrix = Default();
        var before = matrix.Grants.Where(g => g.Role == "requester").ToList();

        matrix.SetRolePermissions("requester", [Permissions.RequestsRead, Permissions.RequestsCreate, Permissions.OrdersRead], Now);

        var after = matrix.Grants.Where(g => g.Role == "requester").ToList();
        after.Select(g => g.Permission).ShouldBe([Permissions.RequestsRead, Permissions.RequestsCreate, Permissions.OrdersRead], ignoreOrder: true);
        // The two grants that stay are the same objects: nothing was deleted and re-inserted.
        after.Where(g => g.Permission == Permissions.RequestsRead).Single().ShouldBeSameAs(before.Single(g => g.Permission == Permissions.RequestsRead));
    }

    [Fact]
    public void Default_matrix_covers_all_permissions_between_its_roles()
    {
        var matrix = Default();

        matrix.Roles.ShouldBe(["admin", "department-head", "finance", "manager", "requester"]);
        matrix.PermissionsOf("finance").ShouldContain(Permissions.BudgetsManage);
        matrix.PermissionsOf("admin").ShouldNotContain(Permissions.RequestsCreate);
    }

    [Fact]
    public void Every_change_records_when_it_happened_so_the_row_is_always_written()
    {
        var matrix = Default();
        var later = Now.AddMinutes(5);

        matrix.SetRolePermissions("requester", [Permissions.RequestsRead], later);

        matrix.UpdatedAt.ShouldBe(later);
    }
}
