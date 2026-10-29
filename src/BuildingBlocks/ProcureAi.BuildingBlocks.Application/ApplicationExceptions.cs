namespace ProcureAi.BuildingBlocks.Application;

/// <summary>The caller's expected version is stale (mapped to HTTP 412 by the Api).</summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);

/// <summary>The item is no longer in a state that allows this (e.g. an approval step that is not pending any more; mapped to HTTP 409 by the Api).</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>The item does not exist, or belongs to another tenant (mapped to HTTP 404 by the Api).</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>The caller is known but may not do this (mapped to HTTP 403 by the Api).</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>
/// A call that must be made as the signed-in user (a model tool of the Assistant) has no credential to relay: there is no current
/// request, or it carries neither a bearer token nor the admin key. The caller turns it into an error result; not an HTTP status.
/// </summary>
public sealed class UserCredentialMissingException(string message) : Exception(message);
