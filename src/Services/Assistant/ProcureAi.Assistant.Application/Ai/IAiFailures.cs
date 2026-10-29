namespace ProcureAi.Assistant.Application.Ai;

/// <summary>
/// Tells a model provider failure that may pass by itself (unreachable, 408/429/5xx, a timeout) from an answer a retry will not change.
/// Implemented next to the provider SDKs (Infrastructure), which own the exception types.
/// </summary>
public interface IAiFailures
{
    bool IsTransient(Exception exception);
}
