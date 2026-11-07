namespace ProcureAi.Assistant.Application.Ai;

/// <summary>
/// The tenant whose daily token total (<c>AI:DailyTokenCapPerTenant</c>) the model calls in this async flow count against. Set for every
/// API request from the caller's tenant (the Api's <c>AiUsageTenantFilter</c>) and by the background work that calls the model for a
/// tenant (ingestion: the document's tenant; past purchases: the request's tenant). The counting stage of every model pipeline reads it;
/// a call outside any scope (the demo seed's guard run before a tenant is known, tests) is not counted.
/// </summary>
public static class AiUsageScope
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The tenant of the current flow, or null.</summary>
    public static string? Tenant => Current.Value;

    /// <summary>Counts the model calls of this flow against <paramref name="tenantId"/> until disposed.</summary>
    public static IDisposable For(string tenantId)
    {
        var previous = Current.Value;
        Current.Value = tenantId;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
