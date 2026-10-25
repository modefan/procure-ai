using ProcureAi.Requests.Application.Abstractions;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>Test caller; tests set the tenant and user before sending a message.</summary>
public sealed class TestCaller : ICallerContext
{
    public string UserId { get; set; } = "user-1";

    public string? TenantId { get; set; } = "acme";
}
