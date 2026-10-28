namespace ProcureAi.Notifications.Application.Presence;

/// <summary>
/// Who is connected and what they watch, shared by every instance (Redis), so any instance can answer and any instance can sweep the
/// connections of another. Times are what the caller passes (the presence clock).
/// </summary>
public interface IPresenceStore
{
    /// <summary>
    /// Records the connection as online at <paramref name="now"/> and marks it viewing every entity it watches (also the ones a sweep had
    /// removed it from: the sweep keeps the list of its watches). Says whether it came online now (a new connection, or one the sweeper
    /// had dropped and that came back) and which entities it watches.
    /// </summary>
    Task<Upserted> UpsertAsync(PresenceConnection connection, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The connection views <paramref name="target"/> as of <paramref name="now"/>. False (and nothing changed) when it already watches
    /// <paramref name="maxWatches"/> other entities.
    /// </summary>
    Task<bool> WatchAsync(PresenceConnection connection, PresenceTarget target, DateTimeOffset now, int maxWatches, CancellationToken cancellationToken);

    /// <summary>The connection no longer views <paramref name="target"/>.</summary>
    Task UnwatchAsync(PresenceConnection connection, PresenceTarget target, CancellationToken cancellationToken);

    /// <summary>A clean disconnect: removes the connection and everything it watched. Null when nothing was known about it.</summary>
    Task<RemovedConnection?> RemoveAsync(string connectionId, CancellationToken cancellationToken);

    /// <summary>
    /// The sweeper's removal, in one atomic step: only while the connection's last heartbeat is still before <paramref name="cutoff"/> (a
    /// heartbeat that arrived after the stale read keeps it), it leaves the online list and the viewer lists. The list of what it watches
    /// is kept (it expires on its own), so a heartbeat that comes later puts it back as a viewer. Null when it was not removed.
    /// </summary>
    Task<RemovedConnection?> RemoveIfStaleAsync(StaleConnection connection, DateTimeOffset cutoff, CancellationToken cancellationToken);

    /// <summary>Every connection of the tenant that is or was online, with its last heartbeat.</summary>
    Task<IReadOnlyList<PresenceEntry>> OnlineAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>Every connection of the tenant that is or was viewing <paramref name="target"/>, with its last heartbeat.</summary>
    Task<IReadOnlyList<PresenceEntry>> ViewersAsync(string tenantId, PresenceTarget target, CancellationToken cancellationToken);

    /// <summary>The connections of every tenant whose last heartbeat is before <paramref name="cutoff"/>, on whichever instance they were.</summary>
    Task<IReadOnlyList<StaleConnection>> StaleAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
