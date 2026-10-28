using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// Lets a request through that carries an <c>X-Api-Key</c> or a bearer token. The gateway does not judge them (it
/// has no key or signing keys of its own to judge by): the service validates the credential and answers 401 itself.
/// </summary>
public sealed class PassThroughAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!BffSchemes.UsesPassThrough(Request))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Not a credential: the client-log endpoint only avoids the cookie handler (see BffSchemes.UsesPassThrough) and reads its session itself.
        if (Request.Path.Equals(ClientLogEndpoints.Path, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(BffSchemes.PassThrough);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), BffSchemes.PassThrough)));
    }
}
