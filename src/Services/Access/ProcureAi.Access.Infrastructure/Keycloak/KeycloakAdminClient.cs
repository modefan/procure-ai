using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Http;

namespace ProcureAi.Access.Infrastructure.Keycloak;

/// <summary>A Keycloak admin call failed unexpectedly. The message names the step and the status, never a token or a response body.</summary>
public sealed class KeycloakAdminException(string message) : Exception(message);

/// <summary>
/// The Keycloak admin API, as the <c>procure-access-admin</c> service account. Every step first looks at what is in place and
/// only changes what is missing (a 409 on the membership counts as done), so a retry after a failure in between is safe.
/// </summary>
public sealed class KeycloakAdminClient(
    HttpClient http,
    [FromKeyedServices(KeycloakAdminSetup.TokenProviderKey)] ServiceTokenProvider tokens,
    IOptions<KeycloakAdminOptions> options) : IIdentityDirectory
{
    private const int MaxOrganizationsListed = 100;

    public string DemoTenantId => options.Value.DemoOrganization;

    public async Task EnsureDemoMemberAsync(string userId, string role, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var realm = $"admin/realms/{Uri.EscapeDataString(settings.Realm)}";
        var user = Uri.EscapeDataString(userId);

        using (var found = await SendAsync(HttpMethod.Get, $"{realm}/users/{user}", null, cancellationToken))
        {
            if (found.StatusCode == HttpStatusCode.NotFound)
            {
                throw new NotFoundException($"User {userId} does not exist in the identity provider.");
            }

            Ensure(found, "reading the user");
        }

        var organizationId = await FindOrganizationIdAsync(realm, settings.DemoOrganization, cancellationToken);
        await EnsureMemberAsync(realm, organizationId, user, userId, cancellationToken);
        await EnsureRoleAsync(realm, user, role, cancellationToken);
    }

    private async Task<string> FindOrganizationIdAsync(string realm, string alias, CancellationToken cancellationToken)
    {
        // The search matches name or domain; the alias is compared here.
        using var response = await SendAsync(
            HttpMethod.Get, $"{realm}/organizations?search={Uri.EscapeDataString(alias)}&exact=false&max={MaxOrganizationsListed}", null, cancellationToken);
        Ensure(response, "finding the organization");
        var organizations = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        foreach (var organization in organizations.EnumerateArray())
        {
            if (organization.GetProperty("alias").GetString() == alias)
            {
                return organization.GetProperty("id").GetString()!;
            }
        }

        throw new KeycloakAdminException($"The organization '{alias}' does not exist in the identity provider.");
    }

    private async Task EnsureMemberAsync(string realm, string organizationId, string escapedUser, string userId, CancellationToken cancellationToken)
    {
        var organization = $"{realm}/organizations/{Uri.EscapeDataString(organizationId)}/members";
        using (var member = await SendAsync(HttpMethod.Get, $"{organization}/{escapedUser}", null, cancellationToken))
        {
            if (member.IsSuccessStatusCode)
            {
                return;
            }

            if (member.StatusCode != HttpStatusCode.NotFound)
            {
                Ensure(member, "checking the membership");
            }
        }

        // The body is the bare user ID as a JSON string.
        using var added = await SendAsync(HttpMethod.Post, organization, JsonContent.Create(userId), cancellationToken);
        if (added.StatusCode != HttpStatusCode.Conflict)
        {
            Ensure(added, "adding the user to the organization");
        }
    }

    private async Task EnsureRoleAsync(string realm, string escapedUser, string role, CancellationToken cancellationToken)
    {
        using var found = await SendAsync(HttpMethod.Get, $"{realm}/roles/{Uri.EscapeDataString(role)}", null, cancellationToken);
        Ensure(found, $"reading the realm role '{role}'");
        var representation = await found.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        // Mapping a role the user already has is a no-op in Keycloak (204).
        using var mapped = await SendAsync(
            HttpMethod.Post, $"{realm}/users/{escapedUser}/role-mappings/realm", JsonContent.Create(new[] { representation }), cancellationToken);
        Ensure(mapped, "assigning the role");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken));
        return await http.SendAsync(request, cancellationToken);
    }

    private static void Ensure(HttpResponseMessage response, string step)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new KeycloakAdminException($"Keycloak refused {step}: status {(int)response.StatusCode}.");
        }
    }
}
