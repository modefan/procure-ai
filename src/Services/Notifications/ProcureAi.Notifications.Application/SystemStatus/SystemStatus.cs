using ProcureAi.BuildingBlocks.Application.Status;

namespace ProcureAi.Notifications.Application.SystemStatus;

/// <summary>A service on the status screen: the worst of its instances' health, or <see cref="Down"/> when no instance wrote a snapshot.</summary>
public enum ServiceHealth
{
    Healthy,
    Degraded,
    Unhealthy,
    Down,
}

/// <summary>
/// One service: its health, how many instances wrote a snapshot within the TTL, its breakers (per name, the worst state of any instance) and
/// its message counts (the newest snapshot's: the message store is shared by the instances; the broker's dead-letter queue by every service).
/// </summary>
public sealed record ServiceStatusDto(
    string Name, ServiceHealth Health, int Instances, IReadOnlyList<BreakerDto> Breakers, WolverineCountsDto Messages);

/// <summary>
/// The system status screen (plan 06 rulings 9 and 10). <see cref="Counters"/> are by name (<c>sagas_running</c>, ...), the caller's tenant's
/// values plus the system-wide ones; <see cref="Alerts"/> likewise (newest first). <see cref="SupplierChaos"/> is null when Purchasing has not
/// reported the switch (it is down, or the simulator did not answer).
/// </summary>
public sealed record SystemStatusDto(
    DateTimeOffset At,
    IReadOnlyList<ServiceStatusDto> Services,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyList<StatusItem> Alerts,
    ChaosDto? SupplierChaos);

/// <summary>Where the snapshots are (Redis), and today's daily stats.</summary>
public interface ISystemStatusSource
{
    /// <summary>Every snapshot that has not expired.</summary>
    Task<IReadOnlyList<StatusSnapshot>> ReadSnapshotsAsync(CancellationToken cancellationToken);

    /// <summary>Duplicate requests coalesced today (South African day), system-wide.</summary>
    Task<long> CoalescedTodayAsync(CancellationToken cancellationToken);
}

/// <summary>Merges the instances' snapshots into the status screen for one tenant.</summary>
public static class SystemStatusAggregator
{
    /// <summary>The most alerts returned (newest first).</summary>
    public const int MaxAlerts = 50;

    /// <param name="expectedServices">Listed even without a snapshot (then <see cref="ServiceHealth.Down"/>), in this order; others follow by name.</param>
    /// <param name="tenantId">The caller's tenant: only its counters and alerts and the system-wide ones (<c>*</c>, or no tenant) are returned.</param>
    public static SystemStatusDto Merge(
        IReadOnlyList<StatusSnapshot> snapshots, IReadOnlyList<string> expectedServices, string tenantId, long coalescedToday, DateTimeOffset now)
    {
        var byService = snapshots.GroupBy(s => s.Service, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var names = expectedServices.Distinct(StringComparer.Ordinal)
            .Concat(byService.Keys.Where(k => !expectedServices.Contains(k, StringComparer.Ordinal)).Order(StringComparer.Ordinal))
            .ToList();

        var services = new List<ServiceStatusDto>();
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        var alerts = new List<StatusItem>();
        ChaosDto? chaos = null;
        foreach (var name in names)
        {
            if (!byService.TryGetValue(name, out var instances))
            {
                services.Add(new ServiceStatusDto(name, ServiceHealth.Down, 0, [], WolverineCountsDto.None));
                continue;
            }

            var newest = instances.MaxBy(s => s.At)!;
            services.Add(new ServiceStatusDto(name, instances.Max(s => HealthOf(s.Health)), instances.Count, WorstBreakers(instances), newest.Messages));

            // Every instance computes the same numbers from the same database: each section is taken once, from the newest snapshot that has it.
            foreach (var section in instances.OrderByDescending(s => s.At).SelectMany(s => s.Sections).DistinctBy(s => s.Name))
            {
                if (section.Name == SupplierChaosSection.Name)
                {
                    chaos = SupplierChaosSection.Read(section) ?? chaos;
                    continue;
                }

                foreach (var (key, value) in section.Counters)
                {
                    if (StatusCounters.TryParse(key, out var tenant, out var counter) && Visible(tenant, tenantId))
                    {
                        counters[counter] = counters.GetValueOrDefault(counter) + value;
                    }
                }

                alerts.AddRange(section.Items.Where(i => i.TenantId is null || i.TenantId == tenantId));
            }
        }

        counters[StatusCounters.CoalescedToday] = coalescedToday;
        return new SystemStatusDto(
            now,
            services,
            counters,
            [.. alerts.DistinctBy(a => (a.Kind, a.Text, a.TenantId, a.At, a.Link)).OrderByDescending(a => a.At).Take(MaxAlerts)],
            chaos);
    }

    private static bool Visible(string counterTenant, string tenantId) =>
        counterTenant == StatusCounters.System || string.Equals(counterTenant, tenantId, StringComparison.Ordinal);

    /// <summary>An unknown health text counts as Unhealthy.</summary>
    private static ServiceHealth HealthOf(string health) =>
        Enum.TryParse<ServiceHealth>(health, ignoreCase: true, out var parsed) && parsed != ServiceHealth.Down ? parsed : ServiceHealth.Unhealthy;

    /// <summary>Per breaker name, the worst state of any instance (Open, then HalfOpen, then Closed); of open ones the latest retry time.</summary>
    private static List<BreakerDto> WorstBreakers(IEnumerable<StatusSnapshot> instances) =>
        [.. instances.SelectMany(s => s.Breakers)
            .GroupBy(b => b.Name, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(b => b.State).ThenByDescending(b => b.RetryAt).First())
            .OrderBy(b => b.Name, StringComparer.Ordinal)];
}

/// <summary>The status endpoint's query.</summary>
public sealed class SystemStatusService(ISystemStatusSource source, TimeProvider time)
{
    public async Task<SystemStatusDto> GetAsync(string tenantId, IReadOnlyList<string> expectedServices, CancellationToken cancellationToken)
    {
        var snapshots = await source.ReadSnapshotsAsync(cancellationToken);
        var coalesced = await source.CoalescedTodayAsync(cancellationToken);
        return SystemStatusAggregator.Merge(snapshots, expectedServices, tenantId, coalesced, time.GetUtcNow());
    }
}
