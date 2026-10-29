using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.Assistant.Application.Guardrails;
using OpenTelemetry.Trace;
using Npgsql;
using ProcureAi.BuildingBlocks.Messaging;
using Wolverine;

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

/// <summary>
/// The real Assistant API on a test server, against the shared infrastructure and the stub Access, on the Fake provider. Ingestion runs
/// in batches of 4 chunks and retries a provider failure after 0.2 s, a completed request without its submit data after 1 s; the embedding generator is wrapped by <paramref name="embeddings"/>
/// and the real prompt guard is wrapped by <see cref="MarkerPromptShield"/>.
/// </summary>
public sealed class AssistantApiFactory(
    AssistantInfraFixture infra, StubAccessServer access, List<Activity> exportedActivities, EmbeddingProbe embeddings)
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
        builder.UseSetting("Assistant:Ingestion:BatchSize", "4");
        builder.UseSetting("Assistant:Ingestion:RetryDelays", "00:00:00.200, 00:00:00.200, 00:00:00.200");
        // A completion that arrives before its submit data is retried three times, a second apart (past purchases).
        builder.UseSetting("Assistant:Purchases:RetryDelays", "00:00:01, 00:00:01, 00:00:01");

        builder.ConfigureTestServices(services =>
        {
            // A second exporter on the service's own tracer provider: what reaches it is what the OTLP exporter gets, so a source
            // the service defaults do not trace never shows up here.
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(exportedActivities));
            services.Decorate<IEmbeddingGenerator<string, Embedding<float>>>((inner, _) => new ProbeEmbeddingGenerator(inner, embeddings));
            services.Decorate<IPromptShield>((inner, _) => new MarkerPromptShield(inner));
        });
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

    public IMessageBus Bus => _factory.Services.GetRequiredService<IMessageBus>();

    public RabbitProbe Probe => new(Infra.RabbitConnectionString);

    /// <summary>Publishes to an exchange through the broker, as another service would, with the tenant and user in the headers.</summary>
    public Task PublishAsync(object message, string exchange, string tenant, string user = "tom") =>
        Bus.EndpointFor(new Uri($"rabbitmq://exchange/{exchange}"))
            .SendAsync(message, new DeliveryOptions().WithHeader(CallerHeaders.TenantId, tenant).WithHeader(CallerHeaders.UserId, user))
            .AsTask();

    /// <summary>Rows of the dead-letter table plus messages in the broker's dead-letter queue.</summary>
    public Task<long> DeadLetterCountAsync() => DeadLetters.CountAsync(Infra.DbConnectionString, Infra.RabbitConnectionString);

    /// <summary>The spans the service's tracer provider exported (OpenTelemetry in-memory exporter; lock it to read).</summary>
    public List<Activity> ExportedActivities { get; } = [];

    /// <summary>Every embedding call of the service (see <see cref="EmbeddingProbe"/>).</summary>
    public EmbeddingProbe Embeddings { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await Infra.InitializeAsync();
        Access = await StubAccessServer.StartAsync();
        _factory = new AssistantApiFactory(Infra, Access, ExportedActivities, Embeddings);
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
