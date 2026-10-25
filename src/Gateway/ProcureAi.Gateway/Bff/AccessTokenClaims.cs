using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// The tenant and roles live in the access token (that is what the services read). They are copied onto the
/// session's identity at sign-in and again at every refresh, so the gateway answers /bff/user and computes
/// permissions from what the token currently says. The token comes straight from the provider's token endpoint;
/// it is read here, not trusted from a client.
/// </summary>
public static class AccessTokenClaims
{
    public static void Apply(ClaimsIdentity identity, string accessToken)
    {
        var token = new JsonWebTokenHandler().ReadJsonWebToken(accessToken);
        foreach (var existing in identity.FindAll("tenant").ToList())
        {
            identity.RemoveClaim(existing);
        }

        if (token.TryGetClaim("tenant", out var tenant))
        {
            identity.AddClaim(new Claim("tenant", tenant.Value));
        }

        // Replace the roles, so a role taken away at the provider disappears from the session too.
        foreach (var existing in identity.FindAll("roles").ToList())
        {
            identity.RemoveClaim(existing);
        }

        foreach (var role in token.Claims.Where(c => c.Type == "roles").Select(c => c.Value).Distinct(StringComparer.Ordinal))
        {
            identity.AddClaim(new Claim("roles", role));
        }
    }
}
