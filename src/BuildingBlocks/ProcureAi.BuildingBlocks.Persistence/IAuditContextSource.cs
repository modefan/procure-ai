namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>
/// Implemented by a DbContext to hand the interceptor the caller of ITS scope. The interceptor itself is a singleton
/// (EF Core option instances are shared app-wide), so it must never hold a per-request caller.
/// </summary>
public interface IAuditContextSource
{
    IAuditContext? AuditContext { get; }
}
