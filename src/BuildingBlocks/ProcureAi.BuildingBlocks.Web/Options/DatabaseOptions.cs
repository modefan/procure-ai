using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace ProcureAi.BuildingBlocks.Web.Options;

/// <summary>The <c>Database</c> configuration section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// The largest number of connections the service's Npgsql pool opens to Postgres. Every service's pool plus the Debezium
    /// servers, Keycloak and the reserved connections must fit the server's <c>max_connections</c> (the AppHost and the chart size it).
    /// </summary>
    [Range(1, 100)]
    public int MaxPoolSize { get; set; } = 10;
}

/// <summary>Applies <see cref="DatabaseOptions.MaxPoolSize"/> to the connection string every Npgsql consumer of the service shares.</summary>
public static class PostgresPooling
{
    /// <summary>
    /// Returns <paramref name="connectionString"/> with <c>Maximum Pool Size</c> set. EF Core, Wolverine's message store and the
    /// readiness check read the same string, so Npgsql gives them one data source (one pool) of that size.
    /// </summary>
    public static string WithMaxPoolSize(string connectionString, int maxPoolSize) =>
        new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = maxPoolSize }.ConnectionString;

    /// <summary>
    /// Rewrites <c>ConnectionStrings:&lt;name&gt;</c> with the configured <c>Database:MaxPoolSize</c> (default
    /// <see cref="DatabaseOptions"/>). Call before anything reads the connection string. An out-of-range value is left for the
    /// options validation to reject at startup.
    /// </summary>
    public static void ApplyMaxPoolSize(IConfigurationBuilder builder, IConfiguration configuration, string connectionName)
    {
        var connectionString = configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var size = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()?.MaxPoolSize ?? new DatabaseOptions().MaxPoolSize;
        if (size < 1 || size > 100)
        {
            return;
        }

        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{connectionName}"] = WithMaxPoolSize(connectionString, size),
        });
    }
}
