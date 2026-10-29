using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Correlation;

namespace ProcureAi.BuildingBlocks.Web.Http;

/// <summary>
/// Makes an outgoing call as the person whose request is being handled, so the callee's permission checks see the user and not
/// this service: the incoming <c>Authorization: Bearer</c> token is relayed as is, or, for a call made with the admin key
/// (Claude's testing), the key with <c>X-Tenant-Id</c> and <c>X-On-Behalf-Of-User</c> when present. Always adds
/// <c>X-Calling-Service</c> and the correlation ID. Whatever the outgoing request carried before is replaced, never sent.
/// Without a current request or a credential it throws <see cref="UserCredentialMissingException"/>, so nothing is ever sent
/// under the service's own identity by mistake. A request that carries <b>both</b> a bearer token and an API key is refused the same
/// way (which one the callee honours would be a guess), and a bearer token never brings <c>X-Tenant-Id</c> or
/// <c>X-On-Behalf-Of-User</c> along: the token states tenant and user itself. The credential goes only to the one service this
/// client is for: a request URI whose scheme, host or port is not that of <c>http://&lt;serviceName&gt;</c> (an absolute URI, or
/// <c>//other-host/path</c> resolved against the base address) throws <see cref="InvalidOperationException"/> and nothing is sent.
/// </summary>
public sealed class UserTokenRelayHandler(IHttpContextAccessor accessor, ServiceIdentity service, string serviceName) : DelegatingHandler
{
    private readonly Uri _target = new($"http://{serviceName}");

    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix = ApiKeyDefaults.BearerScheme + " ";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri
            || uri.Scheme != _target.Scheme
            || !string.Equals(uri.Host, _target.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _target.Port)
        {
            throw new InvalidOperationException($"A user credential is only sent to {_target.Authority}, not to {request.RequestUri}.");
        }

        var context = accessor.HttpContext
            ?? throw new UserCredentialMissingException("There is no current request to take the user's credential from.");
        var incoming = context.Request.Headers;
        var authorization = incoming.Authorization.ToString();
        var apiKey = incoming[ApiKeyDefaults.KeyHeader].ToString();
        var hasBearer = authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            && authorization.Length > BearerPrefix.Length;
        if (!hasBearer && apiKey.Length == 0)
        {
            throw new UserCredentialMissingException("The current request carries neither a bearer token nor an API key to relay.");
        }

        if (hasBearer && apiKey.Length > 0)
        {
            throw new UserCredentialMissingException("The current request carries both a bearer token and an API key; refusing to guess which to relay.");
        }

        Set(request, AuthorizationHeader, hasBearer ? authorization : null);
        Set(request, ApiKeyDefaults.KeyHeader, hasBearer ? null : apiKey);
        Set(request, ApiKeyDefaults.TenantHeader, hasBearer ? null : incoming[ApiKeyDefaults.TenantHeader].ToString());
        Set(request, ApiKeyDefaults.OnBehalfOfUserHeader, hasBearer ? null : incoming[ApiKeyDefaults.OnBehalfOfUserHeader].ToString());
        Set(request, ApiKeyDefaults.CallingServiceHeader, service.Name);
        Set(request, CorrelationIdMiddleware.HeaderName, context.Items[CorrelationIdMiddleware.ItemKey] as string);
        return base.SendAsync(request, cancellationToken);
    }

    // Always ours: whatever was put there before is replaced, never sent.
    private static void Set(HttpRequestMessage request, string name, string? value)
    {
        request.Headers.Remove(name);
        if (!string.IsNullOrEmpty(value))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
