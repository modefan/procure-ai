using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace ProcureAi.Simulators;

public enum KeyKind
{
    /// <summary>The simulators' own key (<c>Simulators:ApiKey</c>): what the services use to call the supplier and the ERP.</summary>
    Simulator,

    /// <summary>The one admin API key (<c>Auth:AdminApiKeySha256</c>): Claude's key, for the chaos switch and test hooks.</summary>
    Admin,

    /// <summary>Either key: the chaos switch, which Purchasing sets with the simulators' key (plan 06 ruling 11).</summary>
    AdminOrSimulator,
}

/// <summary>The endpoints of this controller need the simulators' key (<c>supplier/</c> and <c>erp/</c>).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequireSimulatorKeyAttribute : TypeFilterAttribute
{
    public RequireSimulatorKeyAttribute() : base(typeof(ApiKeyFilter)) => Arguments = [KeyKind.Simulator];
}

/// <summary>The endpoints of this controller need the admin API key (<c>api/v1/simulators/*</c>).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequireAdminKeyAttribute : TypeFilterAttribute
{
    public RequireAdminKeyAttribute() : base(typeof(ApiKeyFilter)) => Arguments = [KeyKind.Admin];
}

/// <summary>The endpoints of this controller take the admin API key or the simulators' key (the chaos switch).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequireAdminOrSimulatorKeyAttribute : TypeFilterAttribute
{
    public RequireAdminOrSimulatorKeyAttribute() : base(typeof(ApiKeyFilter)) => Arguments = [KeyKind.AdminOrSimulator];
}

/// <summary>Checks <c>X-Api-Key</c> in constant time (both sides hashed with SHA-256, compared with FixedTimeEquals). No key configured: nothing passes.</summary>
internal sealed class ApiKeyFilter : IAuthorizationFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly KeyKind _kind;
    private readonly byte[][] _expectedHashes;

    public ApiKeyFilter(KeyKind kind, IOptions<SimulatorOptions> simulator, IOptions<AdminAuthOptions> admin)
    {
        _kind = kind;
        byte[]? simulatorHash = string.IsNullOrEmpty(simulator.Value.ApiKey) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(simulator.Value.ApiKey));
        byte[]? adminHash = string.IsNullOrEmpty(admin.Value.AdminApiKeySha256) ? null : Convert.FromHexString(admin.Value.AdminApiKeySha256);
        byte[]?[] accepted = kind switch
        {
            KeyKind.Simulator => [simulatorHash],
            KeyKind.Admin => [adminHash],
            _ => [adminHash, simulatorHash],
        };
        _expectedHashes = [.. accepted.OfType<byte[]>()];
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var presented = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (presented.Length > 0)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
            // Every accepted key is compared (no early exit), each in constant time.
            var match = false;
            foreach (var expected in _expectedHashes)
            {
                match |= CryptographicOperations.FixedTimeEquals(hash, expected);
            }

            if (match)
            {
                return;
            }
        }

        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = _kind switch
            {
                KeyKind.Admin => "The admin API key is required in X-Api-Key.",
                KeyKind.AdminOrSimulator => "The admin API key or the simulator API key is required in X-Api-Key.",
                _ => "A valid simulator API key is required in X-Api-Key.",
            },
        })
        { StatusCode = StatusCodes.Status401Unauthorized };
    }
}
