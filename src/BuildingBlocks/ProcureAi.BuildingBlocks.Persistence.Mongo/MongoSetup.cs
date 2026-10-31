using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using OpenTelemetry.Trace;

namespace ProcureAi.BuildingBlocks.Persistence.Mongo;

public static class MongoSetup
{
    /// <summary>
    /// One <see cref="IMongoClient"/> (singleton, as the driver requires) and the service's <see cref="IMongoDatabase"/>, from the
    /// connection string <paramref name="connectionName"/> (<c>ConnectionStrings:&lt;name&gt;</c>; Aspire's includes the database name).
    /// Driver commands are traced: MongoDB.Driver 3.13 has OpenTelemetry tracing built in (activity source
    /// <see cref="MongoTelemetry.ActivitySourceName"/>, on by default), so the source is only registered here, with no extra package.
    /// </summary>
    public static IServiceCollection AddProcureAiMongo(this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        var connectionString = configuration.GetConnectionString(connectionName)
            ?? throw new InvalidOperationException($"Connection string '{connectionName}' is not configured.");
        var url = new MongoUrl(connectionString);
        var databaseName = string.IsNullOrEmpty(url.DatabaseName) ? connectionName : url.DatabaseName;

        // The client connects lazily, so nothing here needs a running server (the OpenAPI build step creates the host too).
        services.AddSingleton<IMongoClient>(_ => new MongoClient(MongoClientSettings.FromUrl(url)));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
        services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(MongoTelemetry.ActivitySourceName));
        return services;
    }

    /// <summary>
    /// Registers <see cref="TenantCollection{T}"/> for one collection: scoped, bound to the caller of the current request or message.
    /// </summary>
    public static IServiceCollection AddTenantCollection<T>(this IServiceCollection services, string collectionName)
        where T : ITenantDocument =>
        services.AddScoped(sp => new TenantCollection<T>(
            sp.GetRequiredService<IMongoDatabase>().GetCollection<T>(collectionName),
            sp.GetRequiredService<ProcureAi.BuildingBlocks.Application.ICallerContext>(),
            sp.GetService<TimeProvider>()));
}
