using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace ProcureAi.Suppliers.IntegrationTests;

/// <summary>
/// Plan 08 ruling 6: infra/mongo/users.js (run as root on every start: the AppHost's mongo-users, the chart's db-roles Job) gives each MongoDB
/// database its own user and keeps debezium-mongo's least-privilege user <c>debezium</c> (plan 07 Task 4; Suppliers' migration created it until
/// then). It is idempotent and takes a changed password; Suppliers migrates and runs as suppliers_app, which reaches suppliersdb only.
/// </summary>
[Collection(SuppliersCollection.Name)]
public sealed class MongoUsersScriptTests(SuppliersApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Users_script_is_idempotent_and_takes_a_changed_password()
    {
        // The fixture already ran it; running it again changes nothing and fails nothing.
        await app.Infra.RunUsersScriptAsync();
        await app.Infra.RunUsersScriptAsync();
        await ShouldHaveExactlyTheDebeziumRolesAsync();
        await ShouldHaveExactlyAsync(MongoUsers.Suppliers, ["readWrite@suppliersdb"]);
        await ShouldHaveExactlyAsync(MongoUsers.Assistant, ["readWrite@assistantchatdb"]);
        await PingAsAsync(MongoUsers.Debezium, MongoUsers.TestPassword(MongoUsers.Debezium));

        // A rotated password (the user secret / Kubernetes Secret changed) is applied by the next run; the old one stops working.
        var rotated = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var environment = MongoUsers.ScriptEnvironment();
        environment["DEBEZIUM_MONGO_PASSWORD"] = rotated;
        await app.Infra.RunUsersScriptAsync(environment);
        try
        {
            await PingAsAsync(MongoUsers.Debezium, rotated);
            var refused = await Should.ThrowAsync<MongoAuthenticationException>(
                () => PingAsAsync(MongoUsers.Debezium, MongoUsers.TestPassword(MongoUsers.Debezium)));
            refused.Message.ShouldContain("Unable to authenticate", Case.Insensitive);
            await ShouldHaveExactlyTheDebeziumRolesAsync();
        }
        finally
        {
            await app.Infra.RunUsersScriptAsync();
        }
    }

    [Fact]
    public async Task Users_script_without_a_password_fails_loudly()
    {
        var failed = await Should.ThrowAsync<InvalidOperationException>(
            () => app.Infra.RunUsersScriptAsync(MongoUsers.ScriptEnvironment(without: "DB_SUPPLIERS_PASSWORD")));
        failed.Message.ShouldContain("DB_SUPPLIERS_PASSWORD is not set");
    }

    /// <summary>The negative case: Suppliers' user cannot read the Assistant's chat history (nor write it), only its own database.</summary>
    [Fact]
    public async Task Suppliers_user_cannot_read_another_database()
    {
        using var client = new MongoClient(app.Infra.DatabaseConnectionString);
        (await client.GetDatabase("suppliersdb").GetCollection<BsonDocument>("suppliers").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct))
            .ShouldBeGreaterThan(0, "the seeded catalog, readable by its own user");

        var other = client.GetDatabase("assistantchatdb").GetCollection<BsonDocument>("conversations");
        var read = await Should.ThrowAsync<MongoCommandException>(
            () => other.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
        read.Code.ShouldBe(13, "Unauthorized");
        var write = await Should.ThrowAsync<MongoCommandException>(
            () => other.InsertOneAsync(new BsonDocument("x", 1), cancellationToken: Ct));
        write.Code.ShouldBe(13, "Unauthorized");
    }

    private async Task ShouldHaveExactlyTheDebeziumRolesAsync()
    {
        await ShouldHaveExactlyAsync(
            MongoUsers.Debezium, [.. MongoUsers.CapturedDatabases.Select(d => $"read@{d}"), $"{MongoUsers.ChangeStreamRole}@admin"]);

        var admin = app.Infra.Client.GetDatabase("admin");
        var roles = (await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["rolesInfo"] = MongoUsers.ChangeStreamRole, ["showPrivileges"] = true }, cancellationToken: Ct))["roles"].AsBsonArray;
        var role = roles.ShouldHaveSingleItem().AsBsonDocument;
        role["roles"].AsBsonArray.ShouldBeEmpty();
        role["privileges"].AsBsonArray.Select(p => p.ToJson()).ShouldBe(
            MongoUsers.ChangeStreamPrivileges().Select(p => p.ToJson()), ignoreOrder: true);
    }

    private async Task ShouldHaveExactlyAsync(string userName, string[] roles)
    {
        var users = (await app.Infra.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
            new BsonDocument { ["usersInfo"] = new BsonDocument { ["user"] = userName, ["db"] = "admin" } },
            cancellationToken: Ct))["users"].AsBsonArray;
        var user = users.ShouldHaveSingleItem().AsBsonDocument;
        user["roles"].AsBsonArray.Select(r => $"{r["role"].AsString}@{r["db"].AsString}").ShouldBe(roles, ignoreOrder: true, userName);
    }

    // A fresh client each time: MongoDB keeps connections that authenticated before a password change working, so only a new connection
    // shows which password the server accepts now.
    private async Task PingAsAsync(string user, string password)
    {
        using var client = app.Infra.ClientAs(user, password);
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("connectionStatus", 1), cancellationToken: Ct);
    }
}
