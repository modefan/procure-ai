using Testcontainers.Redis;

namespace ProcureAi.Testing;

/// <summary>One Redis container capped with the limits of the AppHost's Redis resource.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private const string Image = "redis:7-alpine";

    private RedisContainer? _container;

    public string ConnectionString => _container!.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        var redis = TestLimits.Section("Redis");
        _container = new RedisBuilder(Image)
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "Redis"))
            .WithCommand("--maxmemory", redis["MaxMemory"]!, "--maxmemory-policy", redis["MaxMemoryPolicy"]!)
            .Build();
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
