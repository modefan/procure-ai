using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Web.Auth;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>Fills in the tag templates of <see cref="CachedGetAttribute"/>.</summary>
public static class CacheTags
{
    private const string NoTenant = "~";

    /// <summary>The tenant part of a tag, the same for a read (template) and a write (<see cref="ICacheInvalidator"/> callers).</summary>
    public static string Tenant(ICurrentUser user) => user.TenantId ?? NoTenant;

    /// <summary>Resolves <c>{tenant}</c> and route values; a template that names a route value the request lacks is skipped.</summary>
    public static string[] Resolve(IReadOnlyList<string> templates, HttpContext http, ICurrentUser user)
    {
        var tags = new List<string>(templates.Count);
        foreach (var template in templates)
        {
            var resolved = template.Replace("{tenant}", Tenant(user), StringComparison.Ordinal);
            foreach (var (name, value) in http.Request.RouteValues)
            {
                resolved = resolved.Replace("{" + name + "}", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            }

            if (!resolved.Contains('{', StringComparison.Ordinal))
            {
                tags.Add(resolved);
            }
        }

        return [.. tags];
    }
}

/// <summary>Drops cached GET responses by tag after a write, so the next read cannot return the old state.</summary>
public interface ICacheInvalidator
{
    Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);
}

internal sealed class FusionCacheInvalidator(IFusionCache cache) : ICacheInvalidator
{
    public async Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        foreach (var tag in tags)
        {
            await cache.RemoveByTagAsync(tag, token: cancellationToken);
        }
    }
}
