using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>Fills in the tag templates of <see cref="CachedGetAttribute"/> for a request. The tag itself is built by <see cref="CacheTag"/>.</summary>
public static class CacheTags
{
    /// <summary>The tenant part of a tag, the same for a read (template) and a write (<see cref="ICacheInvalidator"/> callers).</summary>
    public static string Tenant(ICurrentUser user) => CacheTag.Tenant(user.TenantId);

    /// <summary>Resolves <c>{tenant}</c> and route values; a template that names a route value the request lacks is skipped.</summary>
    public static string[] Resolve(IReadOnlyList<string> templates, HttpContext http, ICurrentUser user)
    {
        var values = http.Request.RouteValues.Select(v => (v.Key, v.Value)).ToArray();
        return [.. templates.Select(t => Format(t, Tenant(user), values)).OfType<string>()];
    }

    /// <summary>Delegates to <see cref="CacheTag.Format"/>, the single tag builder.</summary>
    public static string? Format(string template, string tenant, params (string Name, object? Value)[] values) =>
        CacheTag.Format(template, tenant, values);
}
