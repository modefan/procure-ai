namespace ProcureAi.Contracts.Access;

/// <summary>Integration event: permissions changed, so caches of them are stale. <c>Kind</c> is <c>Matrix</c>, <c>Organisation</c> or <c>User</c>; <c>UserId</c> is set for <c>User</c>.</summary>
public sealed record AccessChanged(
    string TenantId,
    string Kind,
    string? UserId,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
