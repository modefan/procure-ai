using Medallion.Threading;
using Medallion.Threading.Redis;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.BuildingBlocks.Web.Diagnostics;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.BuildingBlocks.Application.Caching;
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

        var applicationName = builder.Environment.ApplicationName;
        var services = builder.Services;
        services.AddValidatedOptions<FusionCacheSettings>(builder.Configuration, FusionCacheSettings.SectionName);
        services.PostConfigure<FusionCacheSettings>(settings => settings.KeyPrefix ??= applicationName + ":");
        services.AddValidatedOptions<IdempotencyOptions>(builder.Configuration, IdempotencyOptions.SectionName);

        services.AddStackExchangeRedisCache(options => options.Configuration = redis);
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var configuration = ConfigurationOptions.Parse(redis);
            configuration.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(configuration);
        });
        services.Replace(ServiceDescriptor.Singleton<IDailyStats>(provider => new RedisDailyStats(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RedisDailyStats>>(),
            provider.GetService<TimeProvider>())));
        services.AddSingleton<IDistributedLockProvider>(provider =>
            new RedisDistributedSynchronizationProvider(provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase()));

        // Read once here because FusionCache is configured at registration; the same values are validated on start.
        var settings = builder.Configuration.GetSection(FusionCacheSettings.SectionName).Get<FusionCacheSettings>()
                       ?? new FusionCacheSettings();
        services.AddSingleton<ICacheInvalidator, FusionCacheInvalidator>();
        services.AddFusionCache()
            .WithOptions(options =>
            {
                // The Redis backplane channel is "<prefix>:<cache name>"-style; the prefix keeps services' channels apart.
                // (The cache itself keeps FusionCache's default name so plain IFusionCache injection keeps working.)
                options.BackplaneChannelPrefix = settings.BackplaneChannelPrefix ?? applicationName;
                options.CacheKeyPrefix = settings.KeyPrefix ?? applicationName + ":";
            })
            .WithDefaultEntryOptions(options =>
            {
                options.Duration = settings.DefaultDuration;
                options.DistributedCacheDuration = settings.DefaultDuration + settings.DistributedTtlPadding;
            })
            .WithSystemTextJsonSerializer(new System.Text.Json.JsonSerializerOptions { IncludeFields = true })
            .WithRegisteredDistributedCache()
            .WithStackExchangeRedisBackplane(options => options.Configuration = redis);
        return builder;
    }
}
