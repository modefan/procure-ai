using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>Local test credentials, generated per run. Nothing here talks to Keycloak or the network.</summary>
public static class TestAuth
{
    public const string Issuer = "https://issuer.test/realms/procure";
    public const string Audience = "procure-api-test";

    public static string ApiKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string ApiKeySha256 { get; } = Hash(ApiKey);

    public static SymmetricSecurityKey SigningKey { get; } = new(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

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
