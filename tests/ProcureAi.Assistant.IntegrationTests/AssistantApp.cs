using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// Postgres (the custom image with pgvector), MongoDB, RabbitMQ and Redis for the Assistant, with <c>assistantdb</c> migrated by the real
/// <c>--migrate</c> run of the API (which also gets the chat database's connection string, as the AppHost gives it).
/// </summary>
public sealed class AssistantInfraFixture() : ServiceInfraFixture(Database, "policy_documents", "audit_entries")
{
    public const string Database = "assistantdb";
    public const string ChatDatabase = "assistantchatdb";
    public const string ApiAssembly = "ProcureAi.Assistant.Api.dll";

    protected override bool UsesMongo => true;

    public string ChatConnectionString => MongoConnectionStringFor(ChatDatabase);

    protected override Task MigrateAsync(string connectionString) =>
        ApiMigrateProcess.RunAsync(
            ApiAssembly, Database, connectionString, RabbitConnectionString, RedisConnectionString, demoSeed: true,
            environment: new Dictionary<string, string> { [$"ConnectionStrings__{ChatDatabase}"] = ChatConnectionString });
}

/// <summary>The real Assistant API on a test server, against the shared infrastructure and the stub Access, on the Fake provider.</summary>
public sealed class AssistantApiFactory(AssistantInfraFixture infra, StubAccessServer access)
    : ServiceApiFactory<Program>(AssistantInfraFixture.Database, infra.DbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in access.Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting($"ConnectionStrings:{AssistantInfraFixture.ChatDatabase}", infra.ChatConnectionString);
        builder.UseSetting("AI:Fake:StreamDelayMs", "0");
        builder.UseSetting("RateLimiting:PermitLimit", "100000");
    }
}

/// <summary>One running Assistant API for the whole collection. Tests use a tenant of their own.</summary>
public sealed class AssistantApp : IAsyncLifetime
{
    private AssistantApiFactory _factory = null!;

    public AssistantInfraFixture Infra { get; } = new();

    public StubAccessServer Access { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        await Infra.InitializeAsync();
        Access = await StubAccessServer.StartAsync();
        _factory = new AssistantApiFactory(Infra, Access);
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

    public static string NewTenant() => $"t{Guid.NewGuid():N}"[..12];

    public static string Token(string tenant, string role = "requester", string user = "rita") => TestAuth.CreateToken(user, tenant, role);

    public static HttpRequestMessage Get(string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    /// <summary>A single value straight from the database (no tenant filter); null when there is no row.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Infra.DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>The values of the first column of every row.</summary>
    public async Task<List<string>> ColumnAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Infra.DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetValue(0).ToString()!);
        }

        return values;
    }
}

[CollectionDefinition(Name)]
public sealed class AssistantCollection : ICollectionFixture<AssistantApp>
{
    public const string Name = "assistant-infra";
}
