using System.Text.Json;
using Testcontainers.Redis;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>One Redis container for the caching tests, capped with the same limits as the AppHost's Redis resource.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string ConnectionString => _container!.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "testcontainers.settings.json")));
        var redis = settings.RootElement.GetProperty("Resources").GetProperty("Redis");
        var memoryBytes = redis.GetProperty("MemoryMb").GetInt64() * 1024 * 1024;
        var cpus = redis.GetProperty("Cpus").GetInt64();
        var maxMemory = redis.GetProperty("MaxMemory").GetString()!;
        var policy = redis.GetProperty("MaxMemoryPolicy").GetString()!;

        _container = new RedisBuilder("redis:7-alpine")
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig ??= new();
                p.HostConfig.Memory = memoryBytes;
                p.HostConfig.MemorySwap = memoryBytes;
                p.HostConfig.NanoCPUs = cpus * 1_000_000_000;
            })
            .WithCommand("--maxmemory", maxMemory, "--maxmemory-policy", policy)
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

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
