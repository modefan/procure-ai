using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Persistence.Mongo;

namespace ProcureAi.Suppliers.Infrastructure;

/// <summary>
/// Configuration section <c>Mongo</c>: the least-privilege MongoDB user <c>debezium</c> that <c>debezium-mongo</c> connects as (plan 07 Task 4).
/// Only <c>--migrate</c> reads it (the API does not need it), and the migration refuses to run without it.
/// </summary>
public sealed class DebeziumUserOptions
{
    public const string SectionName = "Mongo";

    /// <summary>The user's password (<c>Mongo:DebeziumPassword</c>): the AppHost parameter <c>debezium-mongo-password</c>, in Kubernetes Secret
    /// <c>procure-debezium</c> key <c>mongo-password</c>, the same value debezium-mongo signs in with.</summary>
    [Required]
    public string DebeziumPassword { get; set; } = "";

    /// <summary>Every MongoDB database debezium-mongo captures (<c>Mongo:DebeziumDatabases</c>; the AppHost passes them from the services'
    /// captured collections): the user gets <c>read</c> on exactly these.</summary>
    [Required]
    [MinLength(1)]
    public string[] DebeziumDatabases { get; set; } = [];
}

/// <summary>
/// Creates or updates the MongoDB user <c>debezium</c> (<see cref="DebeziumMongoUser"/>). Suppliers' migration does it because it runs with the
/// server's root rights and is the first migration debezium-mongo waits for. Idempotent: every run sets the password and the roles again.
/// </summary>
internal sealed class CreateDebeziumUser(IOptions<DebeziumUserOptions> options) : IMongoMigration
{
    public Task ApplyAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        // .Value validates (data annotations) and throws OptionsValidationException naming the missing setting.
        var settings = options.Value;
        return DebeziumMongoUser.EnsureAsync(database.Client, settings.DebeziumPassword, settings.DebeziumDatabases, cancellationToken);
    }
}
