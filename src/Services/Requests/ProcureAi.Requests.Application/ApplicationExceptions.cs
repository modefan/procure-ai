namespace ProcureAi.Requests.Application;

/// <summary>The caller's expected version is stale (mapped to HTTP 412 by the Api).</summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);

/// <summary>The request does not exist, or belongs to another tenant (mapped to HTTP 404 by the Api).</summary>
public sealed class NotFoundException(string message) : Exception(message);
