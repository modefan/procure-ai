namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>
/// Who is saving and for which tenant. Each service adapts its own caller abstraction to this, so the
/// persistence building block does not depend on ASP.NET Core or on the web building blocks.
/// </summary>
public interface IAuditContext
{
    string UserId { get; }

    string? TenantId { get; }
}
