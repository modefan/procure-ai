namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>Waiting for an identical request running elsewhere took longer than the configured timeout.</summary>
internal sealed class LockTimeoutException() : Exception("Timed out waiting for the same request running elsewhere.");
