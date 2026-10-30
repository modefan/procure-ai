using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcureAi.BuildingBlocks.Application.Status;

/// <summary>
/// What a service adds to its status snapshot besides health, breakers and message counts (plan 06 ruling 10): business counters and
/// alert items. Called every <c>Status:PublishInterval</c> as the system (no caller); it must be cheap and see every tenant, keying its
/// counters by tenant. A failure (it throws) leaves the section out of that snapshot; the next interval tries again.
/// </summary>
public interface IStatusContributor
{
    /// <summary>The section's name (e.g. <c>approvals</c>).</summary>
    string Name { get; }

    Task<StatusSection> CollectAsync(CancellationToken ct);
}

/// <summary>
/// One contributor's part of a snapshot. <paramref name="Counters"/> are keyed <c>"&lt;tenant&gt;:&lt;name&gt;"</c>, or
/// <c>"*:&lt;name&gt;"</c> for a system-wide value (<see cref="StatusCounters.Key"/>).
/// </summary>
public sealed record StatusSection(string Name, IReadOnlyDictionary<string, long> Counters, IReadOnlyList<StatusItem> Items);

/// <summary>An alert line (e.g. a change made outside the app). <paramref name="TenantId"/> null: system-wide.</summary>
public sealed record StatusItem(string Kind, string Text, string? TenantId, DateTimeOffset At, string? Link);

/// <summary>The state of a circuit breaker as the status screen shows it.</summary>
public enum CircuitState
{
    Closed,

    /// <summary>One trial call is let through; its outcome closes or reopens the circuit.</summary>
    HalfOpen,

    Open,
}

/// <summary>A breaker in a snapshot; <paramref name="RetryAt"/> is when an open circuit lets the next trial call through.</summary>
public sealed record BreakerDto(string Name, CircuitState State, DateTimeOffset? RetryAt);

/// <summary>
/// Wolverine's message store counts (<c>IMessageStore.Admin.FetchCountsAsync()</c>; zero for a service without a store) and the depth of the
/// broker's dead-letter queue <c>wolverine-dead-letter-queue</c> (shared by every service on the broker).
/// </summary>
public sealed record WolverineCountsDto(long Incoming, long Outgoing, long Scheduled, long DeadLetters, long BrokerDeadLetters)
{
    public static WolverineCountsDto None { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// What one running instance of a service writes to Redis every <c>Status:PublishInterval</c> (key <see cref="StatusKeys.Snapshot"/>, TTL
/// <c>Status:Ttl</c>). <paramref name="Health"/> is the result of its readiness checks: <c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.
/// </summary>
public sealed record StatusSnapshot(
    string Service,
    string InstanceId,
    DateTimeOffset At,
    string Health,
    IReadOnlyList<BreakerDto> Breakers,
    WolverineCountsDto Messages,
    IReadOnlyList<StatusSection> Sections);

/// <summary>The breakers of this process, as their Polly callbacks report them (<c>OnOpened</c>, <c>OnClosed</c>, <c>OnHalfOpened</c>).</summary>
public interface ICircuitBreakerStates
{
    void Report(string name, CircuitState state, DateTimeOffset? retryAt);

    /// <summary>Every known breaker (registered ones start Closed), by name.</summary>
    IReadOnlyList<BreakerDto> Snapshot();
}

/// <summary>How a chaos switch makes the simulated supplier misbehave (the simulator's modes).</summary>
public enum ChaosMode
{
    Normal,
    Slow,
    Failing,
    Down,
}

/// <summary>The supplier's chaos mode and when the simulator restores Normal by itself (null in Normal).</summary>
public sealed record ChaosDto(ChaosMode Mode, DateTimeOffset? RestoresAt);

/// <summary>Counter keys and the names the status screen knows.</summary>
public static class StatusCounters
{
    /// <summary>The tenant part of a system-wide counter.</summary>
    public const string System = "*";

    public const string SagasRunning = "sagas_running";
    public const string TimeoutsNextHour = "timeouts_next_hour";
    public const string FinanceAlertsOpen = "finance_alerts_open";
    public const string OutsideAppAlertsOpen = "outside_app_alerts_open";
    public const string GuardBlocksToday = "guard_blocks_today";
    public const string QuestionsToday = "questions_today";
    public const string CoalescedToday = "coalesced_today";

    /// <summary><c>"&lt;tenant&gt;:&lt;name&gt;"</c>; a null tenant gives <c>"*:&lt;name&gt;"</c>.</summary>
    public static string Key(string? tenantId, string name) => (string.IsNullOrEmpty(tenantId) ? System : tenantId) + ":" + name;

    /// <summary>Splits a key at its last colon (tenant IDs never hold one, names neither); false for a key without one.</summary>
    public static bool TryParse(string key, out string tenant, out string name)
    {
        var colon = key.LastIndexOf(':');
        if (colon <= 0 || colon == key.Length - 1)
        {
            tenant = name = "";
            return false;
        }

        tenant = key[..colon];
        name = key[(colon + 1)..];
        return true;
    }
}

/// <summary>
/// The section Purchasing's contributor reports the supplier's chaos switch in (so Notifications shows it without calling Purchasing):
/// <c>*:mode</c> (the <see cref="ChaosMode"/> as a number) and, while it is not Normal, <c>*:restores_at</c> (Unix milliseconds).
/// </summary>
public static class SupplierChaosSection
{
    public const string Name = "supplier-chaos";
    public const string Mode = "mode";
    public const string RestoresAt = "restores_at";

    public static StatusSection From(ChaosDto chaos)
    {
        var counters = new Dictionary<string, long> { [StatusCounters.Key(null, Mode)] = (long)chaos.Mode };
        if (chaos.RestoresAt is { } restoresAt)
        {
            counters[StatusCounters.Key(null, RestoresAt)] = restoresAt.ToUnixTimeMilliseconds();
        }

        return new StatusSection(Name, counters, []);
    }

    public static ChaosDto? Read(StatusSection section)
    {
        if (section.Name != Name || !section.Counters.TryGetValue(StatusCounters.Key(null, Mode), out var mode) || !Enum.IsDefined((ChaosMode)mode))
        {
            return null;
        }

        DateTimeOffset? restoresAt = section.Counters.TryGetValue(StatusCounters.Key(null, RestoresAt), out var at)
            ? DateTimeOffset.FromUnixTimeMilliseconds(at)
            : null;
        return new ChaosDto((ChaosMode)mode, restoresAt);
    }
}

/// <summary>Where snapshots live in Redis, and how they are written: shared by the publisher (every service) and the reader (Notifications).</summary>
public static class StatusKeys
{
    /// <summary>
    /// A sorted set of the snapshot keys, scored by when each expires (Unix milliseconds). The reader takes the members that have not
    /// expired instead of scanning the whole keyspace (which holds every cache entry) on each read.
    /// </summary>
    public const string Index = "procure:status:index";

    /// <summary><c>procure:status:{service}:{instanceId}</c>.</summary>
    public static string Snapshot(string service, string instanceId) =>
        string.Create(CultureInfo.InvariantCulture, $"procure:status:{service}:{instanceId}");

    /// <summary>Web defaults (camelCase) with enums as names, like the APIs.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
