using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>Asks the Access service to put a self-registered user into the demo company.</summary>
public interface IRegistrationClient
{
    /// <summary>True when Access answered 200 or 201 (the user is in the demo company now or already was).</summary>
    Task<bool> RegisterAsync(string userId, string email, string displayName, CancellationToken cancellationToken);
}

/// <summary>The gateway's call to <c>POST internal/registrations</c>, made as the gateway's service client (not as the user).</summary>
public sealed class RegistrationClient(HttpClient http, IOptions<RegistrationOptions> options) : IRegistrationClient
{
    public async Task<bool> RegisterAsync(string userId, string email, string displayName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/access/internal/registrations")
        {
            Content = JsonContent.Create(new { userId, email, displayName }),
        };
        request.Options.Set(ServiceTokenHandler.TenantOption, options.Value.DemoTenantId);
        // One key per call: the retries of this call repeat it (Access answers them from the first result).
        request.Headers.Add("Idempotency-Key", $"register-{Guid.NewGuid():N}");
        using var response = await http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
