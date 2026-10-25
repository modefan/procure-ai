using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>
/// Micro-caches a GET response and runs identical concurrent requests (across all instances) only once.
/// Never put it on a write: other methods pass through uncached.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class CachedGetAttribute : Attribute, IFilterFactory
{
    /// <summary>Cache duration in seconds; 0 (the default) uses <c>Caching:DefaultGetSeconds</c>.</summary>
    public int Seconds { get; set; }

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        ActivatorUtilities.CreateInstance<CachedGetFilter>(serviceProvider, Seconds);
}
