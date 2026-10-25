using System.Globalization;

namespace ProcureAi.BuildingBlocks.Application.Caching;

/// <summary>
/// The one place a cache tag is built, for reads (the tag templates of <c>[CachedGet]</c>) and for invalidation after a
/// write (HTTP controllers and message handlers), so the two can never drift.
/// </summary>
public static class CacheTag
{
    private const string NoTenant = "~";

    /// <summary>The tenant part of a tag; a caller without a tenant shares one marker.</summary>
    public static string Tenant(string? tenantId) => tenantId ?? NoTenant;

    /// <summary>
    /// Fills <c>{tenant}</c> and the named placeholders of <paramref name="template"/>. GUID values are written in
    /// lower-case "D" form whatever case the URL used. Null when a placeholder has no value.
    /// </summary>
    public static string? Format(string template, string tenant, params (string Name, object? Value)[] values)
    {
        var resolved = template.Replace("{tenant}", tenant, StringComparison.Ordinal);
        foreach (var (name, value) in values)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (Guid.TryParse(text, out var guid))
            {
                text = guid.ToString("D");
            }

            resolved = resolved.Replace("{" + name + "}", text, StringComparison.OrdinalIgnoreCase);
        }

        return resolved.Contains('{', StringComparison.Ordinal) ? null : resolved;
    }
}
