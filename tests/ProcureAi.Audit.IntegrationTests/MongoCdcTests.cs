using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using Shouldly;
using AuditInfrastructure = ProcureAi.Audit.Infrastructure.DependencyInjection;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// MongoDB change data capture (plan 06 ruling 16) end to end: a MongoDB replica set, the MongoDB Debezium Server with
/// infra/debezium/application-mongo.properties, the <c>cdc</c> exchange and the Audit service's <c>audit-cdc-mongo</c> queue. Writes through
/// <see cref="TenantCollection{T}"/> (the services' only MongoDB write path) are stamped and attributed to the caller; a change made
/// with mongosh is flagged "changed outside the app" and raises an alert.
/// </summary>
[Collection(AuditCollection.Name)]
public sealed class MongoCdcTests(AuditApp app)
{
    private const string SuppliersDb = "suppliersdb";
    private const string Suppliers = "suppliers";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Supplier_change_through_app_is_attributed()
    {
        var mongo = await app.EnsureMongoCdcAsync();
        var tenant = AuditApp.NewTenant();
        var suppliers = SuppliersAs(mongo, tenant, "tom");
        var key = $"{tenant}:contoso-hardware";

        await suppliers.InsertOneAsync(new SupplierRow { Id = key, TenantId = tenant, Name = "Contoso Hardware", Email = "orders@contoso.test" }, Ct);
        await suppliers.ReplaceOneAsync(
            s => s.Id == key, new SupplierRow { Id = key, TenantId = tenant, Name = "Contoso Hardware", Email = "sales@contoso.test" },
            cancellationToken: Ct);
        await suppliers.DeleteOneAsync(s => s.Id == key, Ct);

        // Insert, replace, the deleting stamp and the delete: every one is the app's, made for tom in the tenant.
        var changes = await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Database == SuppliersDb && c.Table == Suppliers && c.PrimaryKey == key),
            found => found.Count == 4);
        changes.ShouldAllBe(c => c.Attribution.Kind == AttributionKind.App && c.Attribution.UserId == "tom", Describe(changes));
        changes.ShouldAllBe(c => c.TenantId == tenant && c.TransactionId == null);
        changes.Select(c => c.Operation).ShouldBe(["Insert", "Update", "Update", "Delete"], ignoreOrder: true);
        var replace = changes.Single(c => c.Operation == "Update" && c.Stamp is { Deleting: false });
        using (var after = JsonDocument.Parse(replace.After!))
        {
            after.RootElement.GetProperty("Email").GetString().ShouldBe("sales@contoso.test");
        }

        (await app.QueryAsync<OutsideAppAlert>(q => q.Where(a => a.TenantId == tenant))).ShouldBeEmpty();
    }

    /// <summary>
    /// Final review I1: an admin-key write on behalf of a user (the demo seeder, or Claude) shows the user it was for and
    /// <c>claude-admin</c> as who acted, as the same write to Postgres does.
    /// </summary>
    [Fact]
    public async Task Admin_key_write_on_behalf_of_a_user_names_the_actor()
    {
        var mongo = await app.EnsureMongoCdcAsync();
        var tenant = AuditApp.NewTenant();
        var key = $"{tenant}:northwind-traders";
        var suppliers = new TenantCollection<SupplierRow>(
            mongo.Client.GetDatabase(SuppliersDb).GetCollection<SupplierRow>(Suppliers),
            new Caller(tenant, "maria", "claude-admin", "demo-data", CallerKind.AdminApiKey));

        await suppliers.InsertOneAsync(new SupplierRow { Id = key, TenantId = tenant, Name = "Northwind Traders", Email = "sales@northwind.test" }, Ct);

        var change = (await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Database == SuppliersDb && c.PrimaryKey == key),
            found => found.Count == 1)).Single();
        change.Attribution.Kind.ShouldBe(AttributionKind.App);
        change.Attribution.UserId.ShouldBe("maria");
        change.Attribution.ActorId.ShouldBe("claude-admin");
        change.Attribution.CallingService.ShouldBe("demo-data");
    }

    [Fact]
    public async Task Mongosh_change_is_flagged_outside_app()
    {
        var mongo = await app.EnsureMongoCdcAsync();
        var tenant = AuditApp.NewTenant();
        var key = $"{tenant}:fabrikam-office";
        await SuppliersAs(mongo, tenant, "tom").InsertOneAsync(
            new SupplierRow { Id = key, TenantId = tenant, Name = "Fabrikam Office", Email = "sales@fabrikam.test" }, Ct);

        // Someone with database access changes the bank e-mail by hand.
        await mongo.MongoshAsync(SuppliersDb, $"db.suppliers.updateOne({{_id: '{key}'}}, {{$set: {{Email: 'pay@attacker.test'}}}})");

        var changes = await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Database == SuppliersDb && c.PrimaryKey == key),
            found => found.Any(c => c.Operation == "Update"));
        changes.Single(c => c.Operation == "Insert").Attribution.Kind.ShouldBe(AttributionKind.App);
        var outside = changes.Single(c => c.Operation == "Update");
        outside.Attribution.ShouldBe(Attribution.OutsideApp);
        outside.TenantId.ShouldBe(tenant);
        ProcureAi.Audit.Application.Queries.DataChangeView.Fields(outside.Before, outside.After)
            .ShouldContain(new ProcureAi.Audit.Application.Dtos.FieldChangeDto("Email", "sales@fabrikam.test", "pay@attacker.test"));

        // An alert at once (no grace period: there is no transaction to wait for), on the tenant's status.
        var alert = (await app.WaitForAsync<OutsideAppAlert>(q => q.Where(a => a.Id == outside.Id), found => found.Count == 1)).Single();
        alert.TenantId.ShouldBe(tenant);
        alert.Table.ShouldBe(Suppliers);
        alert.WithdrawnAt.ShouldBeNull();
    }

    /// <summary>
    /// Review finding (fix round 1): an update's <c>after</c> is looked up when Debezium reads the event. A bulk delete read late (Debezium
    /// paused, as when it lags) finds every document gone; the deleting stamps must still be read from the updates themselves, so neither
    /// the stamps nor the deletes raise an alert.
    /// </summary>
    [Fact]
    public async Task Bulk_delete_read_late_is_attributed_to_the_app()
    {
        var mongo = await app.EnsureMongoCdcAsync();
        var tenant = AuditApp.NewTenant();
        var suppliers = SuppliersAs(mongo, tenant, "tom");
        var keys = Enumerable.Range(1, 20).Select(i => $"{tenant}:s{i:D2}").ToList();
        await suppliers.InsertManyAsync(keys.Select(k => new SupplierRow { Id = k, TenantId = tenant, Name = k, Email = "x@y.test" }), Ct);
        await app.WaitForAsync<DataChange>(q => q.Where(c => c.Database == SuppliersDb && c.TenantId == tenant), found => found.Count == keys.Count);

        var debezium = app.Infra.MongoDebezium;
        await debezium.PauseAsync();
        try
        {
            // Let a getMore that was waiting when the container froze come back empty, so every event below is read after the unpause.
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            (await SuppliersAs(mongo, tenant, "maria").DeleteManyAsync(Builders<SupplierRow>.Filter.Empty, Ct)).DeletedCount.ShouldBe(keys.Count);
        }
        finally
        {
            await debezium.UnpauseAsync();
        }

        var changes = await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Database == SuppliersDb && c.TenantId == tenant),
            found => found.Count(c => c.Operation == "Delete") == keys.Count && found.Count(c => c.Operation == "Update") == keys.Count,
            TimeSpan.FromSeconds(90));
        changes.Where(c => c.Operation != "Insert").ShouldAllBe(
            c => c.Attribution.Kind == AttributionKind.App && c.Attribution.UserId == "maria", Describe(changes));
        (await app.QueryAsync<OutsideAppAlert>(q => q.Where(a => a.TenantId == tenant))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Postgres_queue_ignores_mongo_keys()
    {
        await app.EnsureMongoCdcAsync();

        // Debezium's Postgres keys are <database>.public.<table>, the MongoDB keys mongo.<database>.<collection>: each queue gets its own.
        (await BindingKeysAsync(AuditInfrastructure.CdcQueue)).ShouldBe([AuditInfrastructure.CdcExchange + " " + "*.public.*"]);
        (await BindingKeysAsync(AuditInfrastructure.CdcMongoQueue)).ShouldBe([AuditInfrastructure.CdcExchange + " " + "mongo.#"]);
    }

    /// <summary>
    /// Plan 07 Task 4: Debezium reads the change streams as the least-privilege user <c>debezium</c> (the fixture creates it the way
    /// Suppliers' --migrate does), not as root, and the capture still works: the server lists its change stream cursor under that user and
    /// an app write reaches the audit.
    /// </summary>
    [Fact]
    public async Task Mongo_cdc_works_as_least_privilege_debezium_user()
    {
        var mongo = await app.EnsureMongoCdcAsync();
        app.Infra.MongoDebezium.User.ShouldBe(DebeziumMongoUser.UserName);

        var operations = await (await mongo.Client.GetDatabase("admin").AggregateAsync<BsonDocument>(
            new[] { new BsonDocument("$currentOp", new BsonDocument { ["allUsers"] = true, ["idleCursors"] = true }) },
            cancellationToken: Ct)).ToListAsync(Ct);
        var changeStreams = operations.Where(op => op.ToJson().Contains("$changeStream", StringComparison.Ordinal)).ToList();
        changeStreams.ShouldNotBeEmpty();
        changeStreams.ShouldAllBe(op => op["effectiveUsers"].AsBsonArray.Single()["user"].AsString == DebeziumMongoUser.UserName);

        var tenant = AuditApp.NewTenant();
        var key = $"{tenant}:least-privilege";
        await SuppliersAs(mongo, tenant, "tom").InsertOneAsync(
            new SupplierRow { Id = key, TenantId = tenant, Name = "Least Privilege Ltd", Email = "lp@test" }, Ct);
        var change = (await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Database == SuppliersDb && c.PrimaryKey == key),
            found => found.Count == 1)).Single();
        change.Operation.ShouldBe("Insert");
        change.Attribution.Kind.ShouldBe(AttributionKind.App);
    }

    /// <summary>The <c>debezium</c> user only reads: a write to a captured database as that user is refused (Unauthorized, code 13).</summary>
    [Fact]
    public async Task Debezium_user_cannot_write()
    {
        var mongo = await app.EnsureMongoCdcAsync();
        using var debezium = mongo.ClientAs(app.Infra.MongoDebezium.User, app.Infra.MongoDebezium.Password);

        // It can read (what the change streams need) ...
        await debezium.GetDatabase(SuppliersDb).GetCollection<BsonDocument>(Suppliers).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        // ... but not write, in either captured database, nor create a user.
        foreach (var (database, collection) in new[] { (SuppliersDb, Suppliers), ("assistantchatdb", "conversations") })
        {
            var refused = await Should.ThrowAsync<MongoCommandException>(() => debezium.GetDatabase(database).GetCollection<BsonDocument>(collection)
                .InsertOneAsync(new BsonDocument("_id", Guid.NewGuid().ToString()), cancellationToken: Ct));
            refused.Code.ShouldBe(13, $"{database}.{collection}: {refused.Message}");
        }

        var noAdmin = await Should.ThrowAsync<MongoCommandException>(() => debezium.GetDatabase("admin").RunCommandAsync<BsonDocument>(
            new BsonDocument { ["createUser"] = "intruder", ["pwd"] = "x", ["roles"] = new BsonArray() }, cancellationToken: Ct));
        noAdmin.Code.ShouldBe(13, noAdmin.Message);

        // The cluster-wide find of debeziumChangeStreams covers non-system collections only: the users and their password hashes stay unreadable.
        var noUsers = await Should.ThrowAsync<MongoCommandException>(() => debezium.GetDatabase("admin").GetCollection<BsonDocument>("system.users")
            .Find(FilterDefinition<BsonDocument>.Empty).FirstOrDefaultAsync(Ct));
        noUsers.Code.ShouldBe(13, noUsers.Message);
    }

    // ----- helpers -----

    private static string Describe(IEnumerable<DataChange> changes) =>
        string.Join("; ", changes.Select(c => $"{c.Operation} {c.Attribution.Kind} {c.Attribution.UserId} {c.Stamp}"));

    private static TenantCollection<SupplierRow> SuppliersAs(MongoFixture mongo, string tenant, string user) =>
        new(mongo.Client.GetDatabase(SuppliersDb).GetCollection<SupplierRow>(Suppliers), new Caller(tenant, user));

    /// <summary>The bindings of a queue (without the default exchange) as "exchange routing-key", from RabbitMQ's management API.</summary>
    private async Task<List<string>> BindingKeysAsync(string queue)
    {
        var amqp = new Uri(app.Infra.RabbitConnectionString);
        using var http = new HttpClient { BaseAddress = app.Infra.RabbitManagementUri };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(amqp.UserInfo))));
        var bindings = await http.GetFromJsonAsync<JsonElement>($"api/queues/%2F/{queue}/bindings", Ct);
        return [.. bindings.EnumerateArray()
            .Where(b => b.GetProperty("source").GetString()!.Length > 0)
            .Select(b => b.GetProperty("source").GetString() + " " + b.GetProperty("routing_key").GetString())];
    }

    /// <summary>A supplier document as the Suppliers service stores it (the fields these tests touch), with its audit stamp.</summary>
    private sealed class SupplierRow : ITenantDocument, IAuditedDocument
    {
        [BsonId]
        public string Id { get; set; } = null!;

        public string TenantId { get; set; } = null!;

        public string Name { get; set; } = null!;

        public string Email { get; set; } = null!;

        [BsonElement(AuditStamp.ElementName)]
        [BsonIgnoreIfNull]
        public AuditStamp? Audit { get; set; }
    }

    private sealed class Caller(string tenant, string user, string? actor = null, string? callingService = null, CallerKind kind = CallerKind.User)
        : ICallerContext
    {
        public string UserId => user;

        public string ActorId => actor ?? user;

        public string? TenantId => tenant;

        public string? CallingService => callingService;

        public CallerKind Kind => kind;
    }
}
