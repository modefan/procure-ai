using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ProcureAi.Gateway.IntegrationTests;

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

    private IContainer? _container;

    /// <summary>The secret of the gateway client for this run (random; the realm file holds only a placeholder).</summary>
    public string GatewaySecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

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
              "defaultClientScopes": ["profile", "email", "roles", "procure-api"]
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

    /// <summary>Password-grant login with the test-only client; returns the decoded access token payload.</summary>
    public async Task<JsonElement> LoginAsync(string user)
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync(
            $"{BaseUrl}/realms/{Realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = TestClient,
                ["username"] = user,
                ["password"] = DemoPassword,
            }));
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"Token request for {user} failed: {(int)response.StatusCode} {body}");
        var token = JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
        return DecodePayload(token);
    }

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
