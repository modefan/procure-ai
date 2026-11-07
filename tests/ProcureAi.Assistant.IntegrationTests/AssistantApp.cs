using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.Assistant.Application.Guardrails;
using ProcureAi.Assistant.Infrastructure.Ai;
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

    protected override Task MigrateAsync(string connectionString) => MigrateAsync(connectionString, ChatConnectionString);

    /// <summary>
    /// Another Postgres database and another chat database on the same servers, migrated and demo-seeded like the shared ones (for a test
    /// that must not touch them). Returns both connection strings.
    /// </summary>
    public async Task<(string Database, string Chat)> CreateSeededDatabasesAsync(string name)
    {
        var database = await CreateEmptyDatabaseAsync(name);
        var chat = MongoConnectionStringFor(name + "_chat");
        await MigrateAsync(database, chat);
        return (database, chat);
    }

    private Task MigrateAsync(string connectionString, string chat) =>
        ApiMigrateProcess.RunAsync(
            ApiAssembly, Database, connectionString, RabbitConnectionString, RedisConnectionString, demoSeed: true,
            environment: new Dictionary<string, string> { [$"ConnectionStrings__{ChatDatabase}"] = chat });
}

/// <summary>
/// The real Assistant API on a test server, against the shared infrastructure, the stub Access and the stub services the tools call as the
/// user (<paramref name="services"/>: Requests, Approvals, Budgets, Suppliers), on the Fake provider. A tool call gives up after 2 s.
/// Ingestion runs in batches of 4 chunks and retries a provider failure after 0.2 s, a completed request without its submit data after
/// 1 s; the embedding generator is wrapped by <paramref name="embeddings"/> and the real prompt guard is wrapped by
/// <see cref="MarkerPromptShield"/>.
/// </summary>
public sealed class AssistantApiFactory(
    AssistantInfraFixture infra, StubAccessServer access, IEnumerable<KeyValuePair<string, string?>> services, List<Activity> exportedActivities,
    EmbeddingProbe embeddings, ChatScript script, ChatFaults faults, (string Database, string Chat)? databases = null,
    IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? configureServices = null)
    : ServiceApiFactory<Program>(
        AssistantInfraFixture.Database, databases?.Database ?? infra.DbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    /// <summary>The client of our own services' tokens (<c>azp</c>): Invoicing reads a stored scan with one.</summary>
    public const string InternalClientId = "procure-internal-test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Auth:InternalClientId", InternalClientId);
        foreach (var (key, value) in access.Settings.Concat(services))
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting($"ConnectionStrings:{AssistantInfraFixture.ChatDatabase}", databases?.Chat ?? infra.ChatConnectionString);
        builder.UseSetting("AI:Fake:StreamDelayMs", "0");
        // A provider that stops answering fails the call after 2 s without an update (the scripted "hangs" question).
        builder.UseSetting("AI:Chat:Timeout", "00:00:02");
        // The tests fail the provider on purpose (ChatScript.Down/Hangs) across one shared app: the model circuit breakers must not open
        // between them and fail unrelated tests. The breaker itself is covered by AiSetupTests.
        builder.UseSetting("AI:Resilience:MinimumThroughput", "1000");
        builder.UseSetting("RateLimiting:PermitLimit", "100000");
        builder.UseSetting("Assistant:Ingestion:BatchSize", "4");
        builder.UseSetting("Assistant:Ingestion:RetryDelays", "00:00:00.200, 00:00:00.200, 00:00:00.200");
        // A completion that arrives before its submit data is retried three times, a second apart (past purchases).
        builder.UseSetting("Assistant:Purchases:RetryDelays", "00:00:01, 00:00:01, 00:00:01");
        // A tool call that hangs is given up after 2 s (the stub Budgets can hang; its client's own timeouts are longer).
        builder.UseSetting("Assistant:Tools:Timeout", "00:00:02");

        // A test's own settings last, so they win over the defaults above.
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            // A second exporter on the service's own tracer provider: what reaches it is what the OTLP exporter gets, so a source
            // the service defaults do not trace never shows up here.
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(exportedActivities));
            services.Decorate<IEmbeddingGenerator<string, Embedding<float>>>((inner, _) => new ProbeEmbeddingGenerator(inner, embeddings));
            services.Decorate<IPromptShield>((inner, _) => new MarkerPromptShield(inner));
            // The provider's chat model is recorded and can be steered per question (ChatScript); the guard classifier stays the plain Fake.
            services.Decorate<ProviderClients>((inner, _) => new ProviderClients(new ScriptedChatClient(inner.Chat, script), inner.Embeddings, inner.Guard));
            // Store and lock faults for the tenants a test lists (ChatFaults).
            services.Decorate<IConversationStore>((inner, _) => new FaultyConversationStore(inner, faults));
            services.Decorate<IAnswerLock>((inner, _) => new FaultyAnswerLock(inner, faults));
            configureServices?.Invoke(services);
        });
    }
}

/// <summary>One running Assistant API for the whole collection. Tests use a tenant of their own.</summary>
public sealed class AssistantApp : IAsyncLifetime
{
    private AssistantApiFactory _factory = null!;

    public AssistantInfraFixture Infra { get; } = new();

    public StubAccessServer Access { get; private set; } = null!;

    /// <summary>The services the tools call as the user (plan 05 Task 5b); each records the relayed headers.</summary>
    public StubRequestsServer Requests { get; private set; } = null!;

    public StubApprovalsServer Approvals { get; private set; } = null!;

    public StubBudgetsServer Budgets { get; private set; } = null!;

    public StubSuppliersServer Suppliers { get; private set; } = null!;

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

    /// <summary>Every chat call that reached the provider, and the per-question steering of the Fake (see <see cref="ChatScript"/>).</summary>
    public ChatScript Script { get; } = new();

    /// <summary>Store and lock faults per tenant (see <see cref="ChatFaults"/>).</summary>
    public ChatFaults Faults { get; } = new();

    /// <summary>Every embedding call of the service (see <see cref="EmbeddingProbe"/>).</summary>
    public EmbeddingProbe Embeddings { get; } = new();

    private IEnumerable<KeyValuePair<string, string?>> StubSettings =>
        Requests.Settings.Concat(Approvals.Settings).Concat(Budgets.Settings).Concat(Suppliers.Settings);

    /// <summary>
    /// Another instance of the API on databases of its own (the caller disposes it), with settings and services of the test's own, and the
    /// spans it exports in <paramref name="activities"/>.
    /// </summary>
    public AssistantApiFactory CreateInstance(
        (string Database, string Chat) databases, IReadOnlyDictionary<string, string?> settings, Action<IServiceCollection>? configureServices = null,
        List<Activity>? activities = null) =>
        new(Infra, Access, StubSettings, activities ?? [], Embeddings, Script, Faults, databases, settings, configureServices);

    public async ValueTask InitializeAsync()
    {
        await Infra.InitializeAsync();
        Access = await StubAccessServer.StartAsync();
        Requests = await StubRequestsServer.StartAsync();
        Approvals = await StubApprovalsServer.StartAsync();
        Budgets = await StubBudgetsServer.StartAsync();
        Suppliers = await StubSuppliersServer.StartAsync();
        _factory = new AssistantApiFactory(Infra, Access, StubSettings, ExportedActivities, Embeddings, Script, Faults);
        Client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        foreach (var stub in new IAsyncDisposable?[] { Access, Requests, Approvals, Budgets, Suppliers })
        {
            if (stub is not null)
            {
                await stub.DisposeAsync();
            }
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
