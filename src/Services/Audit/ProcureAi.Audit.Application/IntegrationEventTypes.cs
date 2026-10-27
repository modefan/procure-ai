using System.Reflection;
using ProcureAi.Contracts;

namespace ProcureAi.Audit.Application;

/// <summary>
/// Every integration event type of <c>ProcureAi.Contracts</c>, found by reflection, so an event added to the contracts is
/// recorded without touching this service. Contract names are unique (the dictionary throws at startup otherwise).
/// </summary>
public static class IntegrationEventTypes
{
    public static IReadOnlyList<Type> All { get; } = typeof(IIntegrationEvent).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IIntegrationEvent).IsAssignableFrom(t))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToArray();

    /// <summary>By contract name (<c>Type.Name</c>), as <see cref="Domain.BusinessEvent.Type"/> stores it.</summary>
    public static IReadOnlyDictionary<string, Type> ByName { get; } = All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    private static readonly Dictionary<Type, PropertyInfo?> RequestIds = All.ToDictionary(
        t => t, t => t.GetProperty("RequestId", typeof(Guid)));

    /// <summary>The purchase request the event names (<c>RequestId</c>), when it has one.</summary>
    public static Guid? RequestIdOf(IIntegrationEvent @event) =>
        RequestIds.TryGetValue(@event.GetType(), out var property) && property is not null
            ? (Guid?)property.GetValue(@event)
            : null;
}
