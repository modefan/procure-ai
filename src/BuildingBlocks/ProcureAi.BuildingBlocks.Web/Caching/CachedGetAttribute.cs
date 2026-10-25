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

    /// <summary>
    /// Tags the cached response is stored under, so a write can drop it with <see cref="ICacheInvalidator"/>. Templates may
    /// use <c>{tenant}</c> (the caller's tenant) and any route value, e.g. <c>requests:{tenant}:{id}</c>.
    /// A template whose route value is missing from the request is skipped.
    /// </summary>
    public string[] Tags { get; set; } = [];

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        ActivatorUtilities.CreateInstance<CachedGetFilter>(serviceProvider, Seconds, Tags);
}
