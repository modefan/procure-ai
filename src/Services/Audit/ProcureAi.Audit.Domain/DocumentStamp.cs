namespace ProcureAi.Audit.Domain;

/// <summary>
/// The <c>_audit: { userId, actorId, callingService, correlationId, at, deleting }</c> stamp a service writes into every MongoDB document it changes (plan 06
/// ruling 16), as Audit read it from a change event.
/// </summary>
/// <param name="UserId">The person the write was for.</param>
/// <param name="CorrelationId">The request or message that caused it, when known.</param>
/// <param name="At">When the app wrote it.</param>
/// <param name="Deleting">Written right before the app deletes the document: the delete that follows is the app's.</param>
/// <param name="ActorId">Who performed the write (<c>claude-admin</c> for the admin key on behalf of a user); null on a stamp written
/// before the actor was stamped (final review I1), which then means the user.</param>
/// <param name="CallingService">The service that made the call, when known.</param>
public sealed record DocumentStamp(
    string UserId, string? CorrelationId, DateTimeOffset At, bool Deleting, string? ActorId = null, string? CallingService = null)
{
    /// <summary>Who performed the write: <see cref="ActorId"/>, else the user.</summary>
    public string Actor => ActorId ?? UserId;
}
