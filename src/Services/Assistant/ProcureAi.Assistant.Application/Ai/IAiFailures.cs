namespace ProcureAi.Assistant.Application.Ai;

/// <summary>
/// Tells a model provider failure that may pass by itself (unreachable, 408/429/5xx, a timeout) from an answer a retry will not change.
/// Implemented next to the provider SDKs (Infrastructure), which own the exception types.
/// </summary>
public interface IAiFailures
{
    bool IsTransient(Exception exception);
}

/// <summary>
/// Whether a failure is a passing database failure that the shared scheduled retry of every service handles (the Messaging building
/// block's transient-database rule). Implemented in Infrastructure, which may reference that rule.
/// </summary>
public interface IDatabaseFailures
{
    bool IsTransientDatabaseFailure(Exception exception);
}
