using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ProcureAi.BuildingBlocks.Web.Auth;

/// <summary>Checks a presented admin API key against the configured SHA-256, in constant time.</summary>
public sealed class ApiKeyValidator
{
    private readonly byte[]? _expected;

    public ApiKeyValidator(IOptions<AuthOptions> options)
    {
        var hex = options.Value.AdminApiKeySha256;
        _expected = string.IsNullOrEmpty(hex) ? null : Convert.FromHexString(hex);
    }

    public bool IsValid(string? presented)
    {
        if (_expected is null || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(hash, _expected);
    }

    /// <summary>True when the request carries the correct admin API key.</summary>
    public bool IsValid(HttpRequest request) =>
        request.Headers.TryGetValue(ApiKeyDefaults.KeyHeader, out var values) && IsValid(values.ToString());
}
