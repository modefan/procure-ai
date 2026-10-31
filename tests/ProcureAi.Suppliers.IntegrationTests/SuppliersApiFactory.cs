using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace ProcureAi.Suppliers.IntegrationTests;

/// <summary>The commands the application sent to MongoDB, seen through the driver's own command events.</summary>
public sealed class MongoCommandLog
{
    private readonly ConcurrentQueue<(string Command, string Collection)> _commands = new();

    public void Record(CommandStartedEvent started)
    {
        // The command document names the collection as the value of its first element (find: "suppliers"); some commands carry none.
        var collection = started.Command.ElementCount > 0 && started.Command.GetElement(0).Value.IsString
            ? started.Command.GetElement(0).Value.AsString
            : "";
        _commands.Enqueue((started.CommandName, collection));
    }

    public int Count(string command, string collection) => _commands.Count(c => c.Command == command && c.Collection == collection);

    public void Clear() => _commands.Clear();
}

/// <summary>
/// The real Suppliers API (its Program) on a test server, against the shared MongoDB, RabbitMQ and Redis, with the stub Access service.
/// The MongoDB client is replaced by one that logs every command, so a test can count what the application asked the database.
/// </summary>
/// <param name="database">The connection string of another database than the shared one (null: the shared one).</param>
/// <param name="settings">Settings of the test's own, applied last.</param>
public sealed class SuppliersApiFactory(
    SuppliersInfra infra, StubAccessServer access, MongoCommandLog log, string? database = null, IReadOnlyDictionary<string, string?>? settings = null)
    : ServiceApiFactory<Program>("suppliersdb", database ?? infra.DatabaseConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in access.Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting("Auth:InternalClientId", SuppliersApp.InternalClientId);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMongoClient>();
            services.AddSingleton<IMongoClient>(_ =>
            {
                var settings = MongoClientSettings.FromUrl(new MongoUrl(database ?? infra.DatabaseConnectionString));
                settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(log.Record);
                return new MongoClient(settings);
            });
        });
    }

    // There is no Npgsql pool to clear for a MongoDB connection string.
    protected override void ReleaseDatabaseConnections(string databaseConnectionString)
    {
    }
}
