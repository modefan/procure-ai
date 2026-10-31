using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ProcureAi.Testing;

/// <summary>
/// A Keycloak container (same image and limits as the AppHost) that imports the committed realm file.
/// The committed file holds no passwords or secrets: the demo password and gateway secret are random per run and
/// passed in as environment variables, which Keycloak substitutes for the ${...} placeholders at import.
/// The test-only direct-access client (password grant) is added to a copy of the realm here, never to the file.
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Image = "quay.io/keycloak/keycloak:26.8.1"; // keep in step with src/AppHost AppHost.cs
    public const string Realm = "procure";
    public const string TestClient = "test-direct";
    public const string InternalClient = "procure-internal";
    public const string AccessAdminClient = "procure-access-admin";
    public const string DemoLoginClient = "procure-demo-login";

    private IContainer? _container;

    /// <summary>The secret of the gateway client for this run (random; the realm file holds only a placeholder).</summary>
    public string GatewaySecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>The secret of the <c>procure-internal</c> client for this run (random; the realm file holds only a placeholder).</summary>
    public string InternalSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>The secret of the <c>procure-access-admin</c> client (Keycloak admin rights) for this run.</summary>
    public string AccessAdminSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>The secret of the <c>procure-demo-login</c> client (the gateway's one-click demo login) for this run.</summary>
    public string DemoLoginSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public string DemoPassword { get; } = "T" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    public string BaseUrl => $"http://{_container!.Hostname}:{_container.GetMappedPublicPort(8080)}";

    public async ValueTask InitializeAsync()
    {
        var limits = JsonNode.Parse(await File.ReadAllTextAsync("testcontainers.settings.json"))!["Resources"]!["Keycloak"]!;
        var realm = JsonNode.Parse(await File.ReadAllTextAsync("procure-realm.json"))!.AsObject();
        (realm["clients"] ??= new JsonArray()).AsArray().Add(JsonNode.Parse($$"""
            {
              "clientId": "{{TestClient}}",
              "enabled": true,
              "publicClient": true,
              "standardFlowEnabled": false,
              "directAccessGrantsEnabled": true,
              "defaultClientScopes": ["profile", "email", "roles", "procure-api", "organization"]
            }
            """)!);

        _container = new ContainerBuilder(Image)
            .WithPortBinding(8080, true)
            .WithPortBinding(9000, true)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", Convert.ToHexString(RandomNumberGenerator.GetBytes(12)))
            .WithEnvironment("KC_HEALTH_ENABLED", "true")
            .WithEnvironment("JAVA_OPTS_KC_HEAP", (string)limits["JavaHeapOpts"]!)
            .WithEnvironment("KC_DEMO_PASSWORD", DemoPassword)
            .WithEnvironment("KC_GATEWAY_SECRET", GatewaySecret)
            .WithEnvironment("KC_INTERNAL_SECRET", InternalSecret)
            .WithEnvironment("KC_ACCESS_ADMIN_SECRET", AccessAdminSecret)
            .WithEnvironment("KC_DEMO_LOGIN_SECRET", DemoLoginSecret)
            .WithResourceMapping(Encoding.UTF8.GetBytes(realm.ToJsonString()), "/opt/keycloak/data/import/procure-realm.json")
            .WithCommand("start-dev", "--import-realm")
            .WithCreateParameterModifier(p =>
            {
                var host = p.HostConfig!;
                host.Memory = (long)limits["MemoryMb"]! * 1024 * 1024;
                host.MemorySwap = host.Memory;
                host.NanoCPUs = (long)limits["Cpus"]! * 1_000_000_000L;
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/health/ready")))
            .Build();
        await _container.StartAsync();
    }

    /// <summary>
    /// Password-grant login with the test-only client; returns the decoded access token payload. <paramref name="scope"/> is the
    /// <c>scope</c> parameter (e.g. <c>openid organization:globex</c>); null sends none.
    /// </summary>
    public async Task<JsonElement> LoginAsync(string user, string? scope = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = TestClient,
            ["username"] = user,
            ["password"] = DemoPassword,
        };
        if (scope is not null)
        {
            form["scope"] = scope;
        }

        using var http = new HttpClient();
        using var response = await http.PostAsync(
            $"{BaseUrl}/realms/{Realm}/protocol/openid-connect/token", new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        EnsureSuccess(response, $"Token request for {user}", body);
        var token = JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
        return DecodePayload(token);
    }

    /// <summary>The raw access token of the <c>procure-internal</c> service account (client credentials grant).</summary>
    public Task<string> InternalTokenAsync() => ClientCredentialsAsync(InternalClient, InternalSecret);

    /// <summary>The raw access token of the <c>procure-access-admin</c> service account.</summary>
    public Task<string> AccessAdminTokenAsync() => ClientCredentialsAsync(AccessAdminClient, AccessAdminSecret);

    private async Task<string> ClientCredentialsAsync(string clientId, string secret)
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync(
            $"{BaseUrl}/realms/{Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
            }));
        var body = await response.Content.ReadAsStringAsync();
        EnsureSuccess(response, $"Client credentials request for {clientId}", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string what, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{what} failed: {(int)response.StatusCode} {body}");
        }
    }

    /// <summary>An HTTP client for the Keycloak admin API of the realm, signed in as the <c>procure-access-admin</c> service account.</summary>
    public async Task<HttpClient> AdminHttpAsync()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await AccessAdminTokenAsync());
        return http;
    }

    /// <summary>Creates a user (as a self-registered one: no organization, no business role); returns its ID.</summary>
    public async Task<string> CreateUserAsync(string email, string firstName = "New", string lastName = "Person")
    {
        using var http = await AdminHttpAsync();
        using var response = await http.PostAsJsonAsync(
            $"/admin/realms/{Realm}/users",
            new { username = email, email, firstName, lastName, enabled = true, emailVerified = true });
        EnsureSuccess(response, $"Creating user {email}", await response.Content.ReadAsStringAsync());
        return response.Headers.Location!.Segments[^1];
    }

    /// <summary>Creates an organization with this alias (and the same name); returns its ID.</summary>
    public async Task<string> CreateOrganizationAsync(string alias)
    {
        using var http = await AdminHttpAsync();
        using var response = await http.PostAsJsonAsync(
            $"/admin/realms/{Realm}/organizations",
            new { name = alias, alias, enabled = true, domains = new[] { new { name = alias + ".test", verified = false } } });
        EnsureSuccess(response, $"Creating organization {alias}", await response.Content.ReadAsStringAsync());
        return response.Headers.Location!.Segments[^1];
    }

    /// <summary>Makes the user a member of the organization.</summary>
    public async Task AddOrganizationMemberAsync(string organizationId, string userId)
    {
        using var http = await AdminHttpAsync();
        using var response = await http.PostAsJsonAsync($"/admin/realms/{Realm}/organizations/{organizationId}/members", userId);
        EnsureSuccess(response, $"Adding {userId} to organization {organizationId}", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Gives the user a realm role.</summary>
    public async Task AddRealmRoleAsync(string userId, string role)
    {
        using var http = await AdminHttpAsync();
        var found = await http.GetAsync($"/admin/realms/{Realm}/roles/{role}");
        var body = await found.Content.ReadAsStringAsync();
        EnsureSuccess(found, $"Reading role {role}", body);
        using var response = await http.PostAsJsonAsync(
            $"/admin/realms/{Realm}/users/{userId}/role-mappings/realm", new[] { JsonDocument.Parse(body).RootElement });
        EnsureSuccess(response, $"Mapping role {role}", await response.Content.ReadAsStringAsync());
    }

    /// <summary>True when the user is a member of the organization with this ID.</summary>
    public async Task<bool> IsOrganizationMemberAsync(string organizationId, string userId)
    {
        using var http = await AdminHttpAsync();
        using var response = await http.GetAsync($"/admin/realms/{Realm}/organizations/{organizationId}/members/{userId}");
        return response.IsSuccessStatusCode;
    }

    /// <summary>The names of the realm roles assigned directly to the user.</summary>
    public async Task<string[]> RealmRolesOfAsync(string userId)
    {
        using var http = await AdminHttpAsync();
        using var response = await http.GetAsync($"/admin/realms/{Realm}/users/{userId}/role-mappings/realm");
        var body = await response.Content.ReadAsStringAsync();
        EnsureSuccess(response, "Reading role mappings", body);
        return JsonDocument.Parse(body).RootElement.EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToArray();
    }

    public static JsonElement Payload(string jwt) => DecodePayload(jwt);

    private static JsonElement DecodePayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
