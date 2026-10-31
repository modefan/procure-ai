namespace ProcureAi.Audit.Domain;

/// <summary>
/// The <c>_audit: { userId, correlationId, at, deleting }</c> stamp a service writes into every MongoDB document it changes (plan 06
/// ruling 16), as Audit read it from a change event.
/// </summary>
/// <param name="UserId">The person the write was for.</param>
/// <param name="CorrelationId">The request or message that caused it, when known.</param>
/// <param name="At">When the app wrote it.</param>
/// <param name="Deleting">Written right before the app deletes the document: the delete that follows is the app's.</param>
public sealed record DocumentStamp(string UserId, string? CorrelationId, DateTimeOffset At, bool Deleting);
