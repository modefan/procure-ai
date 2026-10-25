using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace ProcureAi.Testing;

/// <summary>One RabbitMQ container capped with the limits of the AppHost's RabbitMQ resource.</summary>
public sealed class RabbitFixture : IAsyncLifetime
{
    private const string Image = "rabbitmq:4.3-management";

    private RabbitMqContainer? _container;

    /// <summary>Optional shared network, set before the fixture starts (e.g. so Debezium can reach the broker by <see cref="NetworkAlias"/>).</summary>
    public INetwork? Network { get; init; }

    public string NetworkAlias { get; init; } = "rabbitmq";

    public string ConnectionString => _container!.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        var limits = TestLimits.Section("RabbitMq");
        var builder = new RabbitMqBuilder(Image)
            .WithEnvironment("RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS", $"-rabbit vm_memory_high_watermark {{absolute,\"{limits["VmMemoryHighWatermarkAbsolute"]}\"}}")
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
