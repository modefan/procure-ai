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
    }

    private static Task<CallerScope> ScopeAsync(CallerKind kind, string userId, params string[] costCenters) =>
        new CallerScopeResolver(new FakeCaller(userId, kind), new FakeAccess(costCenters)).GetAsync(CancellationToken.None);

    [Theory]
    [InlineData(CallerKind.AdminApiKey)]
    [InlineData(CallerKind.Service)]
    public async Task The_admin_key_and_other_services_see_everything_without_asking_Access(CallerKind kind)
    {
        var access = new FakeAccess();
        var scope = await new CallerScopeResolver(new FakeCaller("claude-admin", kind), access).GetAsync(CancellationToken.None);

        scope.All.ShouldBeTrue();
        scope.Allows("someone", "MK-110").ShouldBeTrue();
        access.Calls.ShouldBe(0);
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
            .GetAsync(CancellationToken.None);

        scope.Allows("sipho", "IT-200").ShouldBeTrue();
        scope.Allows("thandi", "IT-200").ShouldBeFalse();
    }
}
