using System.Net;
using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Domain;
using ProcureAi.Access.Infrastructure;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>
/// The nightly demo reset of Access (plan 06 rulings 4 and 5) on a seeded database of its own: matrices, departments, cost centers and
/// users go back to the demo seed (an admin's changes undone, a tenant that is not in the seed gone), with ordinary deletes the interceptor
/// records; the users who registered themselves into the Demo company stay.
/// </summary>
[Collection(AccessInfraCollection.Name)]
public sealed class DemoResetTests(AccessInfraFixture infra)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Demo_reset_clears_business_rows_and_reseeds()
    {
        var database = await infra.CreateSeededDatabaseAsync($"reset_{Guid.NewGuid():N}");
        await using var factory = Factory(database);
        const string other = "other-tenant";
        await using (var db = NewDbContext(database))
        {
            var matrix = await db.PermissionMatrices.IgnoreQueryFilters().SingleAsync(m => m.TenantId == "acme", Ct);
            matrix.SetRolePermissions("requester", [], DateTimeOffset.UtcNow);
            var maria = await db.TenantUsers.IgnoreQueryFilters().SingleAsync(u => u.TenantId == "acme" && u.UserId == DemoUsers.Maria, Ct);
            maria.SetApprovalLimit(Money.Zar(1m));
            db.PermissionMatrices.Add(DefaultPermissionMatrix.CreateFor(other, DateTimeOffset.UtcNow));
            db.TenantUsers.Add(TenantUser.Create(other, DemoUsers.Tom, "Tom", "tom@other.test", ["requester"], Money.Zero, false, []));
            await db.SaveChangesAsync(Ct);
        }

        using var client = factory.CreateClient();
        (await DemoResetProbe.ResetAsync(client, "access")).ShouldBe(HttpStatusCode.NoContent);

        await using (var db = NewDbContext(database))
        {
            var matrices = await db.PermissionMatrices.IgnoreQueryFilters().AsNoTracking().Include(m => m.Grants).ToListAsync(Ct);
            matrices.Select(m => m.TenantId).ShouldBe(DemoSeeder.Tenants, ignoreOrder: true);
            matrices.Single(m => m.TenantId == "acme").PermissionsOf("requester")
                .ShouldBe(DefaultPermissionMatrix.CreateFor("acme", DateTimeOffset.UtcNow).PermissionsOf("requester"), ignoreOrder: true);
            var users = await db.TenantUsers.IgnoreQueryFilters().AsNoTracking().ToListAsync(Ct);
            users.Select(u => (u.TenantId, u.UserId)).ShouldBe(DemoSeeder.Users().Select(u => (u.TenantId, u.UserId)), ignoreOrder: true);
            users.Single(u => u.TenantId == "acme" && u.UserId == DemoUsers.Maria).ApprovalLimit.ShouldBe(Money.Zar(200_000m));
            (await db.CostCenters.IgnoreQueryFilters().CountAsync(Ct)).ShouldBe(DemoSeeder.CostCenters().Count());
            (await db.Departments.IgnoreQueryFilters().CountAsync(Ct)).ShouldBe(DemoSeeder.Departments().Count());
            (await db.Tenants.CountAsync(Ct)).ShouldBe(DemoSeeder.TenantNames.Count);
        }

        // Every deleted row has its interceptor row, all in the reset's one transaction with the seed's inserts (ruling 5).
        var deletedUsers = await DemoResetProbe.DeleteEntriesAsync(database, "tenant_users");
        deletedUsers.Count.ShouldBe(DemoSeeder.Users().Count() + 1);
        (await DemoResetProbe.DeleteEntriesAsync(database, "permission_matrices")).Count.ShouldBe(DemoSeeder.Tenants.Length + 1);
        deletedUsers.Select(d => d.TransactionId).Distinct().ShouldHaveSingleItem();

        (await DemoResetProbe.ResetAsync(client, "access")).ShouldBe(HttpStatusCode.NoContent, "twice is safe");
    }

    [Fact]
    public async Task Access_reset_keeps_self_registered_users()
    {
        var database = await infra.CreateSeededDatabaseAsync($"reset_{Guid.NewGuid():N}");
        await using var factory = Factory(database);
        var registered = Guid.NewGuid().ToString();
        await using (var db = NewDbContext(database))
        {
            // What a self-registration writes (RegisterDemoUser): a requester of the Demo company on the sandbox cost center.
            db.TenantUsers.Add(TenantUser.Create(
                "demo", registered, "New Person", "new.person@example.test", ["requester"], Money.Zero, false, [AccessApiFactory.SandboxCostCenter]));
            // The demo approver's limit, changed by an admin: the seeded user is reset, not kept.
            var approver = await db.TenantUsers.IgnoreQueryFilters().SingleAsync(u => u.TenantId == "demo" && u.UserId == DemoUsers.DemoApprover, Ct);
            approver.SetApprovalLimit(Money.Zar(1m));
            await db.SaveChangesAsync(Ct);
        }

        using var client = factory.CreateClient();
        (await DemoResetProbe.ResetAsync(client, "access")).ShouldBe(HttpStatusCode.NoContent);

        await using (var db = NewDbContext(database))
        {
            var demo = await db.TenantUsers.IgnoreQueryFilters().AsNoTracking().Where(u => u.TenantId == "demo").ToListAsync(Ct);
            var kept = demo.Single(u => u.UserId == registered);
            kept.CostCenterCodes.ShouldBe([AccessApiFactory.SandboxCostCenter]);
            kept.Roles.ShouldBe(["requester"]);
            demo.Single(u => u.UserId == DemoUsers.DemoApprover).ApprovalLimit.ShouldBe(Money.Zar(5_000_000m));
            (await db.CostCenters.IgnoreQueryFilters().AnyAsync(c => c.TenantId == "demo" && c.Code == AccessApiFactory.SandboxCostCenter, Ct))
                .ShouldBeTrue("the sandbox the self-registered users work in is seeded again");
        }

        (await DemoResetProbe.DeleteEntriesAsync(database, "tenant_users")).Select(d => d.Key).ShouldNotContain(k => k.Contains(registered));
    }

    [Fact]
    public async Task Demo_reset_disabled_is_404()
    {
        await using var factory = new AccessApiFactory(infra);
        using var client = factory.CreateClient();

        (await DemoResetProbe.ResetAsync(client, "access")).ShouldBe(HttpStatusCode.NotFound);
        (await DemoResetProbe.ResetAsync(client, "access", TestAuth.CreateServiceToken(AccessApiFactory.InternalClientId)))
            .ShouldBe(HttpStatusCode.NotFound);
    }

    private AccessApiFactory Factory(string database) =>
        new(infra, settings: new Dictionary<string, string?> { ["ConnectionStrings:accessdb"] = database, ["DemoReset:Enabled"] = "true" });

    private static AccessDbContext NewDbContext(string database) =>
        new(new DbContextOptionsBuilder<AccessDbContext>().UseNpgsql(database).Options);
}
