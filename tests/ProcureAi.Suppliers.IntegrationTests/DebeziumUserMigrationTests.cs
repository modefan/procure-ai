using MongoDB.Bson;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using Shouldly;

namespace ProcureAi.Suppliers.IntegrationTests;

/// <summary>
/// Plan 07 Task 4: Suppliers' <c>--migrate</c> (which runs with the server's root rights) creates the least-privilege MongoDB user
/// <c>debezium</c> that <c>debezium-mongo</c> connects as: <c>read</c> on every captured database plus the change-stream role, nothing else.
/// It runs on every deployment, so it must be idempotent and must take a changed password.
/// </summary>
[Collection(SuppliersCollection.Name)]
public sealed class DebeziumUserMigrationTests(SuppliersApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Debezium_user_migration_is_idempotent()
    {
        // The collection's --migrate already created the user; running it again changes nothing and fails nothing.
        await app.Infra.MigrateAsync();
        await app.Infra.MigrateAsync();
        await ShouldHaveExactlyTheDebeziumRolesAsync();
        await ShouldSignInAsync(app.Infra.DebeziumPassword);

        // A rotated password (the user secret / Kubernetes Secret changed) is applied by the next run; the old one stops working.
        var old = app.Infra.DebeziumPassword;
        app.Infra.DebeziumPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        await app.Infra.MigrateAsync();
        await ShouldSignInAsync(app.Infra.DebeziumPassword);
        var refused = await Should.ThrowAsync<MongoAuthenticationException>(() => PingAsAsync(old));
        refused.Message.ShouldContain("Unable to authenticate", Case.Insensitive);
        await ShouldHaveExactlyTheDebeziumRolesAsync();
    }

    [Fact]
    public async Task Migration_without_the_debezium_password_fails_loudly()
    {
        var failed = await Should.ThrowAsync<InvalidOperationException>(() => app.Infra.MigrateWithAsync(
            new Dictionary<string, string> { ["Mongo__DebeziumDatabases__0"] = SuppliersInfra.Database }));
        failed.Message.ShouldContain("DebeziumPassword");
    }

    private async Task ShouldHaveExactlyTheDebeziumRolesAsync()
    {
        var admin = app.Infra.Client.GetDatabase("admin");
        var users = (await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["usersInfo"] = new BsonDocument { ["user"] = DebeziumMongoUser.UserName, ["db"] = "admin" } },
            cancellationToken: Ct))["users"].AsBsonArray;
        var user = users.ShouldHaveSingleItem().AsBsonDocument;
        user["roles"].AsBsonArray.Select(r => $"{r["role"].AsString}@{r["db"].AsString}").ShouldBe(
            [.. SuppliersInfra.DebeziumDatabases.Select(d => $"read@{d}"), $"{DebeziumMongoUser.ChangeStreamRole}@admin"], ignoreOrder: true);

        var roles = (await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["rolesInfo"] = DebeziumMongoUser.ChangeStreamRole, ["showPrivileges"] = true }, cancellationToken: Ct))["roles"].AsBsonArray;
        var role = roles.ShouldHaveSingleItem().AsBsonDocument;
        role["roles"].AsBsonArray.ShouldBeEmpty();
        role["privileges"].AsBsonArray.Select(p => p.ToJson()).ShouldBe(
            DebeziumMongoUser.ChangeStreamPrivileges().Select(p => p.ToJson()), ignoreOrder: true);
    }

    private async Task ShouldSignInAsync(string password) => await PingAsAsync(password);

    // A fresh client each time: MongoDB keeps connections that authenticated before a password change working, so only a new connection
    // shows which password the server accepts now.
    private async Task PingAsAsync(string password)
    {
        using var client = app.Infra.ClientAs(DebeziumMongoUser.UserName, password);
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("connectionStatus", 1), cancellationToken: Ct);
    }
}
