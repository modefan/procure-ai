using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ProcureAi.Testing;

/// <summary>Local test credentials, generated per run. Nothing here talks to Keycloak or the network.</summary>
public static class TestAuth
{
    public const string Issuer = "https://issuer.test/realms/procure";
    public const string Audience = "procure-api-test";

    public static string ApiKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string ApiKeySha256 { get; } = Hash(ApiKey);

    public static SymmetricSecurityKey SigningKey { get; } = new(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>A user token: <c>sub</c>, the <c>tenant</c> claim (when given) and the roles.</summary>
    public static string CreateToken(string subject, string? tenant, params string[] roles) =>
        CreateToken(subject, tenant, null, roles);

    public static string CreateToken(
        string subject, string? tenant, Dictionary<string, object>? extraClaims, params string[] roles)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["roles"] = roles };
        if (tenant is not null)
        {
            claims["tenant"] = tenant;
        }

        return Sign(claims, extraClaims);
    }

    /// <summary>A client-credentials token of another service: the <c>azp</c> claim names the calling client, there is no user.</summary>
    public static string CreateServiceToken(string authorizedParty) =>
        Sign(new Dictionary<string, object> { ["sub"] = $"service-account-{authorizedParty}", ["azp"] = authorizedParty }, null);

    private static string Sign(Dictionary<string, object> claims, Dictionary<string, object>? extraClaims)
    {
        foreach (var (name, value) in extraClaims ?? [])
        {
            claims[name] = value;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }
}
