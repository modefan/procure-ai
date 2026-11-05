using MongoDB.Bson;

namespace ProcureAi.Testing;

/// <summary>
/// The MongoDB users infra/mongo/users.js creates (plan 08 ruling 6; the debezium user since plan 07 Task 4), as the tests expect them, and
/// the test passwords <see cref="MongoFixture"/> gives them. All live in the <c>admin</c> database.
/// </summary>
public static class MongoUsers
{
    /// <summary>Suppliers' own user: <c>readWrite</c> on <c>suppliersdb</c>.</summary>
    public const string Suppliers = "suppliers_app";

    /// <summary>The Assistant's own user: <c>readWrite</c> on <c>assistantchatdb</c>.</summary>
    public const string Assistant = "assistant_app";

    /// <summary>debezium-mongo's user: <c>read</c> on every captured database plus <see cref="ChangeStreamRole"/>.</summary>
    public const string Debezium = "debezium";

    /// <summary>The custom role in <c>admin</c> with the change-stream privileges (built-in roles grant either too little or far more).</summary>
    public const string ChangeStreamRole = "debeziumChangeStreams";

    /// <summary>The databases debezium-mongo captures (the AppHost and the chart pass the same list).</summary>
    public static readonly string[] CapturedDatabases = ["suppliersdb", "assistantchatdb"];

    /// <summary>Each service database and the user that owns it.</summary>
    public static readonly IReadOnlyDictionary<string, string> UserOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["suppliersdb"] = Suppliers,
        ["assistantchatdb"] = Assistant,
    };

    /// <summary>The password <see cref="MongoFixture"/> gives a user of users.js. Not a secret.</summary>
    public static string TestPassword(string user) => "test-only-mongo-" + user;

    /// <summary>
    /// The privileges of <see cref="ChangeStreamRole"/>, as MongoDB's <c>rolesInfo</c> reports them (Debezium 3.7 MongoDB connector documentation,
    /// "Setting up MongoDB": the cluster-wide <c>find</c> and <c>changeStream</c> for <c>capture.scope=deployment</c>, and <c>listDatabases</c>).
    /// </summary>
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

    /// <summary>The environment users.js reads (root sign-in excluded), with the test passwords; a test may leave some out.</summary>
    public static Dictionary<string, string> ScriptEnvironment(params string[] without)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DB_SUPPLIERS_PASSWORD"] = TestPassword(Suppliers),
            ["DB_ASSISTANT_MONGO_PASSWORD"] = TestPassword(Assistant),
            ["DEBEZIUM_MONGO_PASSWORD"] = TestPassword(Debezium),
            ["DEBEZIUM_MONGO_DATABASES"] = string.Join(',', CapturedDatabases),
        };
        foreach (var name in without)
        {
            environment.Remove(name);
        }

        return environment;
    }
}
