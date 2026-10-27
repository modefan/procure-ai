using System.Net.Http.Headers;

namespace ProcureAi.Suppliers.IntegrationTests;

/// <summary>The infrastructure and one running Suppliers API for the whole collection. Tests read the seeded demo catalogs.</summary>
public sealed class SuppliersApp : IAsyncLifetime
{
    public const string Acme = "acme";
    public const string Globex = "globex";
    public const string InternalClientId = "procure-internal";

    private SuppliersApiFactory _factory = null!;

    public SuppliersInfra Infra { get; } = new();

    public StubAccessServer Access { get; private set; } = null!;

    public MongoCommandLog Commands { get; } = new();

    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Infra.InitializeAsync();
        Access = await StubAccessServer.StartAsync();
        _factory = new SuppliersApiFactory(Infra, Access, Commands);
        Client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (Access is not null)
        {
            await Access.DisposeAsync();
        }

        await Infra.DisposeAsync();
    }

    public static HttpRequestMessage Request(string url, string token, string? tenantHeader = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenantHeader);
        }

        return request;
    }

    public static string UserToken(string tenant, string role = "requester", string user = "u1") => TestAuth.CreateToken(user, tenant, role);

    public static string ServiceToken() => TestAuth.CreateServiceToken(InternalClientId);
}

[CollectionDefinition(Name)]
public sealed class SuppliersCollection : ICollectionFixture<SuppliersApp>
{
    public const string Name = "suppliers-infra";
}
