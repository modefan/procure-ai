using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Scope;

public sealed class CallerScopeTests
{
    private sealed class FakeCaller(string userId, CallerKind kind, string? tenant = "acme") : ICallerContext
    {
        public string UserId { get; } = userId;

        public string ActorId => UserId;

        public string? TenantId { get; } = tenant;

        public string? CallingService => null;

        public CallerKind Kind { get; } = kind;
    }

    private sealed class FakeAccess(params string[] costCenters) : IAccessDirectory
    {
        public int Calls { get; private set; }

        public ValueTask<EffectiveAccess> GetEffectiveAccessAsync(
            string userId, IReadOnlyCollection<string> roles, string tenantId, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new EffectiveAccess([], costCenters, 0m));
        }

        public ValueTask<CostCenterApprovers> GetApproversAsync(string costCenterCode, string tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetDisplayNameAsync(string userId, string tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DirectoryUser?> GetUserAsync(string userId, string tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<AccessExplanation?> ExplainAsync(string userId, string tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static Task<CallerScope> ScopeAsync(CallerKind kind, string userId, params string[] costCenters) =>
        new CallerScopeResolver(new FakeCaller(userId, kind), new FakeAccess(costCenters)).GetAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_admin_key_sees_everything_without_asking_Access()
    {
        var access = new FakeAccess();
        var scope = await new CallerScopeResolver(new FakeCaller("claude-admin", CallerKind.AdminApiKey), access)
            .GetAsync(TestContext.Current.CancellationToken);

        scope.All.ShouldBeTrue();
        scope.Allows("someone", "MK-110").ShouldBeTrue();
        access.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_service_acting_for_nobody_sees_everything_without_asking_Access()
    {
        var access = new FakeAccess();
        var scope = await new CallerScopeResolver(new FakeCaller(SystemCaller.Id, CallerKind.Service), access)
            .GetAsync(TestContext.Current.CancellationToken);

        scope.All.ShouldBeTrue();
        access.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_service_acting_on_behalf_of_a_user_gets_that_users_cost_centers()
    {
        var access = new FakeAccess("IT-200");
        var scope = await new CallerScopeResolver(new FakeCaller("tom", CallerKind.Service), access)
            .GetAsync(TestContext.Current.CancellationToken);

        scope.All.ShouldBeFalse();
        scope.AllowsCostCenter("IT-200").ShouldBeTrue();
        scope.AllowsCostCenter("MK-110").ShouldBeFalse();
        access.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_requester_may_see_their_own_request_on_a_foreign_cost_center()
    {
        var scope = await ScopeAsync(CallerKind.User, "sipho", "IT-200");

        scope.Allows("sipho", "MK-110").ShouldBeTrue();
        scope.Allows("thandi", "MK-110").ShouldBeFalse();
    }

    [Fact]
    public async Task A_manager_of_IT_200_sees_IT_200_but_not_MK_110()
    {
        var scope = await ScopeAsync(CallerKind.User, "ben", "IT-200");

        scope.All.ShouldBeFalse();
        scope.Allows("sipho", "IT-200").ShouldBeTrue();
        scope.Allows("sipho", "MK-110").ShouldBeFalse();
        scope.AllowsCostCenter("MK-110").ShouldBeFalse();
    }

    [Fact]
    public async Task Finance_gets_every_cost_center_from_Access()
    {
        var scope = await ScopeAsync(CallerKind.User, "fiona", "IT-200", "MK-110", "OP-300");

        scope.Allows("sipho", "IT-200").ShouldBeTrue();
        scope.Allows("sipho", "MK-110").ShouldBeTrue();
        scope.Allows("sipho", "OP-300").ShouldBeTrue();
    }

    [Fact]
    public async Task A_caller_without_a_tenant_sees_only_their_own_requests()
    {
        var scope = await new CallerScopeResolver(new FakeCaller("sipho", CallerKind.User, tenant: null), new FakeAccess("IT-200"))
            .GetAsync(TestContext.Current.CancellationToken);

        scope.Allows("sipho", "IT-200").ShouldBeTrue();
        scope.Allows("thandi", "IT-200").ShouldBeFalse();
    }
}
