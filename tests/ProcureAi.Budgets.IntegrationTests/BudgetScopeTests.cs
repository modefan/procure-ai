using ProcureAi.Budgets.Api;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using Shouldly;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>Which cost centers each kind of caller sees budgets of (no containers: the scope is built from fakes).</summary>
public sealed class BudgetScopeTests
{
    private sealed class FakeCaller(string userId, CallerKind kind, string? tenant = "acme") : ICallerContext
    {
        public string UserId { get; } = userId;

        public string ActorId => UserId;

        public string? TenantId { get; } = tenant;

        public string? CallingService => null;

        public CallerKind Kind { get; } = kind;
    }

    private sealed class FakeUser(string userId, params string[] roles) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public string UserId { get; } = userId;

        public string? TenantId => "acme";

        public IReadOnlySet<string> Roles { get; } = roles.ToHashSet();

        public bool IsAdminApiKey => false;

        public bool IsServiceAccount => false;

        public string? CallingService => null;

        public string? OnBehalfOfUser => null;
    }

    /// <summary>Access: Tom acts for CC-A; a finance user holds budgets.manage (permissions follow the roles given).</summary>
    private sealed class FakeAccess : IAccessDirectory
    {
        public ValueTask<EffectiveAccess> GetEffectiveAccessAsync(
            string userId, IReadOnlyCollection<string> roles, string tenantId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EffectiveAccess(
                roles.Contains("finance") ? [Permissions.BudgetsRead, Permissions.BudgetsManage] : [Permissions.BudgetsRead],
                userId == "tom" ? ["CC-A"] : [],
                0m));

        public ValueTask<CostCenterApprovers> GetApproversAsync(string costCenterCode, string tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetDisplayNameAsync(string userId, string tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static Task<IReadOnlySet<string>?> VisibleAsync(CallerKind kind, string userId, params string[] roles)
    {
        var caller = new FakeCaller(userId, kind);
        var access = new FakeAccess();
        var scope = new BudgetScope(new CallerScopeResolver(caller, access), caller, new FakeUser(userId, roles), access);
        return scope.VisibleCostCentersAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Budget_scope_for_service_on_behalf_of_user_is_users_scope()
    {
        var visible = await VisibleAsync(CallerKind.Service, "tom");

        visible.ShouldNotBeNull().ShouldBe(["CC-A"]);
    }

    [Fact]
    public async Task Budget_scope_for_service_acting_for_nobody_and_the_admin_key_is_all()
    {
        (await VisibleAsync(CallerKind.Service, SystemCaller.Id)).ShouldBeNull();
        (await VisibleAsync(CallerKind.AdminApiKey, "claude-admin")).ShouldBeNull();
    }

    [Fact]
    public async Task Budget_scope_for_users_is_their_cost_centers_unless_the_token_manages_budgets()
    {
        (await VisibleAsync(CallerKind.User, "tom", "requester")).ShouldNotBeNull().ShouldBe(["CC-A"]);
        (await VisibleAsync(CallerKind.User, "thandi", "finance")).ShouldBeNull();
    }
}
