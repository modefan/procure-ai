using Medallion.Threading;
using Medallion.Threading.Redis;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Web.Options;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Caching;

public static class CachingSetup
{
    public const string RedisConnectionName = "Redis";

    /// <summary>
    /// FusionCache (memory L1, Redis L2 and backplane), the Redis distributed lock provider and the options for
    /// <c>[CachedGet]</c> and <c>[Idempotent]</c>. Needs <c>ConnectionStrings:Redis</c>; fails at startup without it.
    /// </summary>
    public static WebApplicationBuilder AddProcureAiCaching(this WebApplicationBuilder builder)
    {
        var redis = builder.Configuration.GetConnectionString(RedisConnectionName);
        if (string.IsNullOrWhiteSpace(redis))
        {
            throw new InvalidOperationException(
                $"Connection string '{RedisConnectionName}' is required for caching, locking and idempotency.");
        }

        var settings = builder.Configuration.GetSection(FusionCacheSettings.SectionName).Get<FusionCacheSettings>()
                       ?? new FusionCacheSettings();
        var services = builder.Services;
        services.AddValidatedOptions<FusionCacheSettings>(builder.Configuration, FusionCacheSettings.SectionName);
        services.AddValidatedOptions<IdempotencyOptions>(builder.Configuration, IdempotencyOptions.SectionName);

        services.AddStackExchangeRedisCache(options => options.Configuration = redis);
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var configuration = ConfigurationOptions.Parse(redis);
            configuration.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(configuration);
        });
        services.AddSingleton<IDistributedLockProvider>(provider =>
            new RedisDistributedSynchronizationProvider(provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase()));

        services.AddFusionCache()
            .WithOptions(options => options.CacheKeyPrefix = settings.KeyPrefix)
            .WithSystemTextJsonSerializer(new System.Text.Json.JsonSerializerOptions { IncludeFields = true })
            .WithRegisteredDistributedCache()
            .WithStackExchangeRedisBackplane(options => options.Configuration = redis);
        return builder;
    }
}
