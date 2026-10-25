using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>Test caller; tests set the tenant and user before sending a message.</summary>
public sealed class TestCaller : ICallerContext
{
    public string UserId { get; set; } = "user-1";

    public string ActorId { get; set; } = "user-1";

    public string? TenantId { get; set; } = "acme";

    public string? CallingService { get; set; }

    public CallerKind Kind { get; set; } = CallerKind.User;
}

/// <summary>
/// The per-scope caller, registered as a scoped class like the API will register its own. It starts as a copy of the
/// shared <see cref="TestCaller"/> (which tests set before sending a message) and can be changed per scope.
/// </summary>
public sealed class ScopedTestCaller : ICallerContext
{
    public ScopedTestCaller(TestCaller defaults)
    {
        UserId = defaults.UserId;
        ActorId = defaults.ActorId;
        TenantId = defaults.TenantId;
        CallingService = defaults.CallingService;
        Kind = defaults.Kind;
    }

    public string UserId { get; set; }

    public string ActorId { get; set; }

    public string? TenantId { get; set; }

    public string? CallingService { get; set; }

    public CallerKind Kind { get; set; }
}
