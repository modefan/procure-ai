using System.Text.RegularExpressions;
using ProcureAi.Contracts.Notifications;

namespace ProcureAi.Notifications.Application.Presence;

/// <summary>A user as presence shows them: <c>{ userId, name }</c> in the hub's JSON.</summary>
public sealed record Viewer(string UserId, string Name);

/// <summary>One open hub connection: whose it is (from the token, never from the client) and the name to show.</summary>
public sealed record PresenceConnection(string ConnectionId, string TenantId, string UserId, string Name);

/// <summary>A connection as the store holds it, with the time of its last heartbeat (or watch).</summary>
public sealed record PresenceEntry(string ConnectionId, string UserId, string Name, DateTimeOffset LastSeen);

/// <summary>What removing a connection changed: the tenant whose online list and the entities whose viewer lists to send again.</summary>
public sealed record RemovedConnection(string TenantId, IReadOnlyList<PresenceTarget> Targets);

/// <summary>A connection the sweeper found without a heartbeat for longer than the TTL.</summary>
public sealed record StaleConnection(string TenantId, string ConnectionId);

/// <summary>
/// An entity a client watches: one of <see cref="LiveEntities"/> and an ID of 1 to 64 letters, digits, <c>.</c>, <c>_</c> or <c>-</c>
/// (GUIDs, cost center codes). No <c>:</c>, so Redis keys and SignalR group names built from it are unambiguous.
/// </summary>
public readonly partial record struct PresenceTarget(string Entity, string Id)
{
    /// <summary>The target as one string, <c>{entity}:{id}</c>.</summary>
    public string Key => $"{Entity}:{Id}";

    /// <summary>Checks what a client sent; false for an unknown entity or an ID that is not a plain identifier.</summary>
    public static bool TryCreate(string? entity, string? id, out PresenceTarget target)
    {
        target = default;
        if (entity is null || id is null || !LiveEntities.All.Contains(entity, StringComparer.Ordinal) || !SafeId().IsMatch(id))
        {
            return false;
        }

        target = new PresenceTarget(entity, id);
        return true;
    }

    /// <summary>The reverse of <see cref="Key"/>, for keys the store wrote itself; null for anything else.</summary>
    public static PresenceTarget? FromKey(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && TryCreate(key[..colon], key[(colon + 1)..], out var target) ? target : null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}\\z")]
    private static partial Regex SafeId();
}

/// <summary>Turns the store's connections into what clients see.</summary>
public static class PresenceView
{
    /// <summary>The moment before which a heartbeat is too old: <paramref name="now"/> minus the TTL.</summary>
    public static DateTimeOffset Cutoff(DateTimeOffset now, TimeSpan ttl) => now - ttl;

    /// <summary>
    /// The distinct users among the connections still alive at <paramref name="now"/> (last seen no earlier than the cutoff): a user with
    /// two tabs counts once. Sorted by name, then user ID, so every client gets the same order.
    /// </summary>
    public static IReadOnlyList<Viewer> Viewers(IEnumerable<PresenceEntry> entries, DateTimeOffset now, TimeSpan ttl)
    {
        var cutoff = Cutoff(now, ttl);
        return
        [
            .. entries
                .Where(e => e.LastSeen >= cutoff)
                .GroupBy(e => e.UserId, StringComparer.Ordinal)
                .Select(g => new Viewer(g.Key, g.OrderByDescending(e => e.LastSeen).First().Name))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.UserId, StringComparer.Ordinal),
        ];
    }
}
