using System.Net.Http.Headers;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Web.Http;

/// <summary>
/// Makes an outgoing call as this service: its client-credentials token as the bearer, <c>X-Calling-Service</c> with its
/// own name, and, when it acts for a person (not <see cref="SystemCaller.Id"/>), <c>X-On-Behalf-Of-User</c> and
/// <c>X-Tenant-Id</c> so the callee's audit and authorization see who the call is really for. A caller that names the
/// tenant itself (<see cref="TenantOption"/>) wins, for calls where the tenant is an argument and nobody is the caller.
/// </summary>
public sealed class ServiceTokenHandler(
    ServiceTokenProvider tokens, ICallerContextAccessor callers, ServiceIdentity service) : DelegatingHandler
{
    /// <summary>Set on a request to send this tenant as <c>X-Tenant-Id</c> whatever the current caller is.</summary>
    public static readonly HttpRequestOptionsKey<string> TenantOption = new("ProcureAi.TenantId");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var caller = callers.Current;
        var forUser = caller.UserId != SystemCaller.Id;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken));
        Set(request, ApiKeyDefaults.CallingServiceHeader, service.Name);
        Set(request, ApiKeyDefaults.OnBehalfOfUserHeader, forUser ? caller.UserId : null);
        Set(
            request, ApiKeyDefaults.TenantHeader,
            request.Options.TryGetValue(TenantOption, out var tenant) ? tenant : forUser ? caller.TenantId : null);
        return await base.SendAsync(request, cancellationToken);
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
