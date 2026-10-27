using System.Reflection;
using ProcureAi.Contracts;

namespace ProcureAi.Testing;

/// <summary>
/// Guards against a silent gap in a service's messaging: every exchange delivers every event of its contract namespace to the
/// service's queue (Wolverine binds the exchange, not single event types), and an event without a handler is reported as
/// "no handler" at run time instead of being consumed. The check lists, per service, the events it must handle (or
/// handle explicitly as a no-op) so a new event added to a namespace fails a test, not a queue.
/// </summary>
public static class HandlerCoverage
{
    private static readonly string[] HandlerMethodNames = ["Handle", "Consume", "StartOrHandle", "NotFound", "Start", "Orchestrate"];

    /// <summary>
    /// Fails, listing the missing types, unless every <see cref="IIntegrationEvent"/> in <paramref name="contractNamespaces"/>
    /// (for example <c>ProcureAi.Contracts.Approvals</c>) has a public <c>Handle</c>, <c>Consume</c>, <c>StartOrHandle</c> or
    /// <c>NotFound</c> method taking it, in a class of <paramref name="handlers"/> whose name ends in <c>Handler</c> or that is a
    /// Wolverine <c>Saga</c>.
    /// </summary>
    public static void AssertEveryEventHandled(Assembly handlers, params string[] contractNamespaces)
    {
        if (contractNamespaces.Length == 0)
        {
            throw new ArgumentException("Name at least one contract namespace.", nameof(contractNamespaces));
        }

        var events = typeof(IIntegrationEvent).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } or { IsValueType: true })
            .Where(t => typeof(IIntegrationEvent).IsAssignableFrom(t) && contractNamespaces.Contains(t.Namespace))
            .ToList();
        if (events.Count == 0)
        {
            throw new InvalidOperationException($"No integration events found in {string.Join(", ", contractNamespaces)}: wrong namespace?");
        }

        var handled = handlers.GetTypes()
            .Where(t => t.IsPublic && (t.Name.EndsWith("Handler", StringComparison.Ordinal) || IsSaga(t)))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => HandlerMethodNames.Contains(m.Name))
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType))
            .ToHashSet();

        var missing = events.Where(e => !handled.Contains(e)).Select(e => e.FullName).Order().ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"No handler for: {string.Join(", ", missing)}. Add a Handle method to a class named ...Handler (an explicit no-op is fine).");
        }
    }

    private static bool IsSaga(Type type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.Name == "Saga")
            {
                return true;
            }
        }

        return false;
    }
}
