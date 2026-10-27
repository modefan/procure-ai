using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>Asks the Access service to put a self-registered user into the demo company.</summary>
public interface IRegistrationClient
{
    /// <summary>True when Access answered 200 or 201 (the user is in the demo company now or already was).</summary>
    Task<bool> RegisterAsync(string userId, CancellationToken cancellationToken);
}

/// <summary>
/// The gateway's call to <c>POST internal/registrations</c>, made as the gateway's service client on behalf of the signed-in
/// user (<c>X-On-Behalf-Of-User</c>), who is the only user Access will register. Access reads email and name from Keycloak.
/// </summary>
public sealed class RegistrationClient(HttpClient http, IOptions<RegistrationOptions> options) : IRegistrationClient
{
    public async Task<bool> RegisterAsync(string userId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/access/internal/registrations")
        {
            Content = JsonContent.Create(new { userId }),
        };
        request.Options.Set(ServiceTokenHandler.TenantOption, options.Value.DemoTenantId);
        request.Options.Set(ServiceTokenHandler.OnBehalfOfUserOption, userId);
        // One key per user: parallel and repeated calls for the same user run once in Access and share its answer.
        // With 1-day idempotency retention, a user removed from the organization within 24 h gets the stored answer and is not
        // re-joined.
        request.Headers.Add("Idempotency-Key", $"register-{userId}");
        using var response = await http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
