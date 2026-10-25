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

    /// <summary>Tenant, user, route and the query string sorted by name (so parameter order does not matter).</summary>
    public static string ForRead(HttpContext context, ICurrentUser user)
    {
        var query = context.Request.Query
            .SelectMany(pair => pair.Value.Select(value => $"{pair.Key}={value}"))
            .Order(StringComparer.Ordinal);
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        return Build("read", user.TenantId ?? NoTenant, user.UserId, path, string.Join('&', query));
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
