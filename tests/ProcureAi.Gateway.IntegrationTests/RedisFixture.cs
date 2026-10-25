using System.Text.Json;
using Testcontainers.Redis;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>A Redis container capped with the same limits as the AppHost's Redis resource.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string ConnectionString => _container!.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync("testcontainers.settings.json"));
        var redis = settings.RootElement.GetProperty("Resources").GetProperty("Redis");
        var memoryBytes = redis.GetProperty("MemoryMb").GetInt64() * 1024 * 1024;
        var cpus = redis.GetProperty("Cpus").GetInt64();

        _container = new RedisBuilder("redis:7-alpine")
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig ??= new();
                p.HostConfig.Memory = memoryBytes;
                p.HostConfig.MemorySwap = memoryBytes;
                p.HostConfig.NanoCPUs = cpus * 1_000_000_000;
            })
            .WithCommand("--maxmemory", redis.GetProperty("MaxMemory").GetString()!, "--maxmemory-policy", redis.GetProperty("MaxMemoryPolicy").GetString()!)
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
