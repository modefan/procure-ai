namespace ProcureAi.Notifications.Application.Presence;

/// <summary>
/// Who is connected and what they watch, shared by every instance (Redis), so any instance can answer and any instance can sweep the
/// connections of another. Times are what the caller passes (the presence clock).
/// </summary>
public interface IPresenceStore
{
    /// <summary>
    /// Records the connection as online at <paramref name="now"/> and refreshes every entity it watches. True when it was not online
    /// before (a new connection, or one the sweeper had dropped and that came back).
    /// </summary>
    Task<bool> UpsertAsync(PresenceConnection connection, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The connection views <paramref name="target"/> as of <paramref name="now"/>. False (and nothing changed) when it already watches
    /// <paramref name="maxWatches"/> other entities.
    /// </summary>
    Task<bool> WatchAsync(PresenceConnection connection, PresenceTarget target, DateTimeOffset now, int maxWatches, CancellationToken cancellationToken);

    /// <summary>The connection no longer views <paramref name="target"/>.</summary>
    Task UnwatchAsync(PresenceConnection connection, PresenceTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the connection and everything it watched. <paramref name="tenantHint"/> (the sweeper knows it) still removes it from that
    /// tenant's online list when its details are already gone. Null when nothing was known about it.
    /// </summary>
    Task<RemovedConnection?> RemoveAsync(string connectionId, string? tenantHint, CancellationToken cancellationToken);

    /// <summary>Every connection of the tenant that is or was online, with its last heartbeat.</summary>
    Task<IReadOnlyList<PresenceEntry>> OnlineAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>Every connection of the tenant that is or was viewing <paramref name="target"/>, with its last heartbeat.</summary>
    Task<IReadOnlyList<PresenceEntry>> ViewersAsync(string tenantId, PresenceTarget target, CancellationToken cancellationToken);

    /// <summary>The connections of every tenant whose last heartbeat is before <paramref name="cutoff"/>, on whichever instance they were.</summary>
    Task<IReadOnlyList<StaleConnection>> StaleAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
