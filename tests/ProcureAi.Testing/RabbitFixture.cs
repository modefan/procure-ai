using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace ProcureAi.Testing;

/// <summary>One RabbitMQ container capped with the limits of the AppHost's RabbitMQ resource.</summary>
public sealed class RabbitFixture : IAsyncLifetime
{
    private const string Image = "rabbitmq:4.3-management";
    private const int ManagementPort = 15672;

    private RabbitMqContainer? _container;

    /// <summary>Optional shared network, set before the fixture starts (e.g. so Debezium can reach the broker by <see cref="NetworkAlias"/>).</summary>
    public INetwork? Network { get; init; }

    public string NetworkAlias { get; init; } = "rabbitmq";

    public string ConnectionString => _container!.GetConnectionString();

    /// <summary>The management HTTP API (user and password as in <see cref="ConnectionString"/>), e.g. to read a queue's bindings.</summary>
    public Uri ManagementUri => new($"http://{_container!.Hostname}:{_container.GetMappedPublicPort(ManagementPort)}/");

    public async ValueTask InitializeAsync()
    {
        var limits = TestLimits.Section("RabbitMq");
        var builder = new RabbitMqBuilder(Image)
            .WithEnvironment("RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS", $"-rabbit vm_memory_high_watermark {{absolute,\"{limits["VmMemoryHighWatermarkAbsolute"]}\"}}")
            .WithPortBinding(ManagementPort, true)
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "RabbitMq"));
        if (Network is not null)
        {
            builder = builder.WithNetwork(Network).WithNetworkAliases(NetworkAlias);
        }

        _container = builder.Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
