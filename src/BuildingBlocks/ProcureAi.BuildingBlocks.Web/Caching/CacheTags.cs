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
        var values = http.Request.RouteValues.Select(v => (v.Key, v.Value)).ToArray();
        return [.. templates.Select(t => Format(t, Tenant(user), values)).OfType<string>()];
    }

    /// <summary>
    /// The one place a tag is built, for reads (templates) and for invalidation after a write, so the two can never drift.
    /// GUID values are written in lower-case "D" form whatever case the URL used. Null when a placeholder has no value.
    /// </summary>
    public static string? Format(string template, string tenant, params (string Name, object? Value)[] values)
    {
        var resolved = template.Replace("{tenant}", tenant, StringComparison.Ordinal);
        foreach (var (name, value) in values)
        {
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (Guid.TryParse(text, out var guid))
            {
                text = guid.ToString("D");
            }

            resolved = resolved.Replace("{" + name + "}", text, StringComparison.OrdinalIgnoreCase);
        }

        return resolved.Contains('{', StringComparison.Ordinal) ? null : resolved;
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
