using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>
/// The one place cache and lock keys are built. Every key starts with the tenant and the user, so one caller
/// can never be served another's response. Each part is length-prefixed and the result hashed, so no value can
/// forge another key by containing a separator.
/// </summary>
public static class RequestKey
{
    private const string NoTenant = "~";

    /// <summary>
    /// Tenant, user, route (case-sensitive: route values can be case-sensitive IDs) and the query pairs sorted
    /// by name then value, so parameter order does not matter. Every name and value is its own length-prefixed part.
    /// </summary>
    public static string ForRead(HttpContext context, ICurrentUser user)
    {
        var parts = new List<string> { user.TenantId ?? NoTenant, user.UserId, context.Request.Path.Value ?? string.Empty };
        foreach (var (name, value) in context.Request.Query
                     .SelectMany(pair => pair.Value.Select(value => (Name: pair.Key, Value: value ?? string.Empty)))
                     .OrderBy(pair => pair.Name, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Value, StringComparer.Ordinal))
        {
            parts.Add(name);
            parts.Add(value);
        }

        return Build("read", [.. parts]);
    }

    /// <summary>Tenant, user and the client's idempotency key.</summary>
    public static string ForWrite(HttpContext context, ICurrentUser user, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Build("write", user.TenantId ?? NoTenant, user.UserId, idempotencyKey);
    }

    private static string Build(string kind, params string[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            builder.Append('|').Append(part.Length).Append(':').Append(part);
        }

        return kind + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
