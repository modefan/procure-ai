namespace ProcureAi.Audit.Domain;

/// <summary>Who a data change is attributed to.</summary>
public enum AttributionKind
{
    /// <summary>Not decided yet: the change arrived, the interceptor row of its transaction has not (or there is none).</summary>
    Pending,

    /// <summary>Made through the app: the interceptor row of the same transaction names the user.</summary>
    App,

    /// <summary>No interceptor row within the grace period: someone changed the database without the app.</summary>
    OutsideApp,
}

/// <summary>
/// The attribution of a <see cref="DataChange"/>: <c>App(UserId, ActorId, CallingService, CorrelationId, InterceptorEntryId)</c>,
/// <c>Pending</c> or <c>OutsideApp</c>. One flat record (not a class per case) so the store can filter on <see cref="Kind"/> and
/// <see cref="UserId"/>. <see cref="UserId"/> is the person the change was for, <see cref="ActorId"/> who performed it
/// (e.g. tom and <c>claude-admin</c> when the admin key acts on behalf of tom).
/// </summary>
public sealed record Attribution(
    AttributionKind Kind,
    string? UserId,
    string? ActorId,
    string? CallingService,
    string? CorrelationId,
    Guid? InterceptorEntryId)
{
    public static Attribution Pending { get; } = new(AttributionKind.Pending, null, null, null, null, null);

    public static Attribution OutsideApp { get; } = new(AttributionKind.OutsideApp, null, null, null, null, null);

    /// <summary>Made by the app, as the interceptor row of the same transaction says.</summary>
    public static Attribution App(InterceptorEntry entry) =>
        new(AttributionKind.App, entry.UserId, entry.ActorId, entry.CallingService, entry.CorrelationId, entry.Id);

    /// <summary>Made by the app, as the <c>_audit</c> stamp of a MongoDB document says (no interceptor row: the stamp names the user).</summary>
    public static Attribution Stamped(DocumentStamp stamp) =>
        new(AttributionKind.App, stamp.UserId, stamp.UserId, null, stamp.CorrelationId, null);
}
