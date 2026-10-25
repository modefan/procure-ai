namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>The header names that carry the caller across messages. IDs only, never tokens.</summary>
public static class CallerHeaders
{
    public const string CorrelationId = "x-correlation-id";
    public const string TenantId = "x-tenant-id";
    public const string UserId = "x-user-id";
    public const string CallingService = "x-calling-service";

    internal static readonly string[] All = [UserId, TenantId, CorrelationId, CallingService];
}
