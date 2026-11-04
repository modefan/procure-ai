using MongoDB.Bson;
using MongoDB.Driver;

namespace ProcureAi.BuildingBlocks.Persistence.Mongo;

/// <summary>
/// The least-privilege MongoDB user the MongoDB Debezium Server (<c>debezium-mongo</c>) connects as (plan 07 Task 4), instead of the server's
/// root user. Created or updated by Suppliers' <c>--migrate</c> (which runs with root rights) on every run, and by the CDC test fixture the same
/// way.
/// <para>
/// Rights, from the Debezium 3.7 MongoDB connector documentation ("Setting up MongoDB", "How the MongoDB connector works";
/// https://debezium.io/documentation/reference/3.7/connectors/mongodb.html): read access to the captured databases, the <c>hello</c> command
/// (needs no privilege), the <c>listDatabases</c> action, and, because <c>debezium-mongo</c> opens one change stream for the whole replica set
/// (<c>capture.scope=deployment</c>, the default: one connector watches both captured databases), the cluster-wide <c>find</c> and
/// <c>changeStream</c> actions (MongoDB's resource <c>{ db: "", collection: "" }</c>: every non-system collection). No write, no user or role
/// administration, no system collections (so not <c>admin.system.users</c>).
/// </para>
/// </summary>
public static class DebeziumMongoUser
{
    public const string UserName = "debezium";

    /// <summary>The custom role in <c>admin</c> with the change-stream privileges (built-in roles grant either too little or far more).</summary>
    public const string ChangeStreamRole = "debeziumChangeStreams";

    private const string AdminDatabase = "admin";

    /// <summary>The privileges of <see cref="ChangeStreamRole"/>, as MongoDB's <c>rolesInfo</c> reports them.</summary>
    public static IReadOnlyList<BsonDocument> ChangeStreamPrivileges() =>
    [
        new BsonDocument
        {
            ["resource"] = new BsonDocument { ["db"] = "", ["collection"] = "" },
            ["actions"] = new BsonArray { "changeStream", "find" },
        },
        new BsonDocument
        {
            ["resource"] = new BsonDocument { ["cluster"] = true },
            ["actions"] = new BsonArray { "listDatabases" },
        },
    ];

    /// <summary>
    /// Creates <see cref="UserName"/> (in <c>admin</c>) with <paramref name="password"/>, <c>read</c> on each of <paramref name="databases"/> and
    /// <see cref="ChangeStreamRole"/>, or brings an existing one to exactly that (password, roles). Idempotent. Needs a client with the rights
    /// to manage users and roles (the root user).
    /// </summary>
    public static async Task EnsureAsync(IMongoClient client, string password, IReadOnlyCollection<string> databases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (databases.Count == 0 || databases.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The debezium user needs at least one database to read, and no empty name.", nameof(databases));
        }

        var admin = client.GetDatabase(AdminDatabase);

        var privileges = new BsonArray(ChangeStreamPrivileges());
        var roleExists = (await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument("rolesInfo", ChangeStreamRole), cancellationToken: cancellationToken))["roles"].AsBsonArray.Count > 0;
        await admin.RunCommandAsync<BsonDocument>(
            roleExists
                ? new BsonDocument { ["updateRole"] = ChangeStreamRole, ["privileges"] = privileges, ["roles"] = new BsonArray() }
                : new BsonDocument { ["createRole"] = ChangeStreamRole, ["privileges"] = privileges, ["roles"] = new BsonArray() },
            cancellationToken: cancellationToken);

        var roles = new BsonArray(databases.Distinct(StringComparer.Ordinal).Select(d => new BsonDocument { ["role"] = "read", ["db"] = d }))
        {
            new BsonDocument { ["role"] = ChangeStreamRole, ["db"] = AdminDatabase },
        };
        var userExists = (await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument("usersInfo", new BsonDocument { ["user"] = UserName, ["db"] = AdminDatabase }),
            cancellationToken: cancellationToken))["users"].AsBsonArray.Count > 0;
        // updateUser replaces the roles and the password, so a removed database or a rotated password is applied too.
        await admin.RunCommandAsync<BsonDocument>(
            userExists
                ? new BsonDocument { ["updateUser"] = UserName, ["pwd"] = password, ["roles"] = roles }
                : new BsonDocument { ["createUser"] = UserName, ["pwd"] = password, ["roles"] = roles },
            cancellationToken: cancellationToken);
    }
}
