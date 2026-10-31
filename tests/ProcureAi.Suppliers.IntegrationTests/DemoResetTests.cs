using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using ProcureAi.Suppliers.Infrastructure;
using Shouldly;

namespace ProcureAi.Suppliers.IntegrationTests;

/// <summary>
/// The nightly demo reset of Suppliers (plan 06 rulings 4 and 5) on a database of its own: every supplier of every tenant is deleted through
/// <c>TenantCollection</c>, so each delete is preceded by its "deleting" audit stamp (Audit then sees an app delete, never a change made
/// outside the app), and the demo catalogs are seeded again.
/// </summary>
[Collection(SuppliersCollection.Name)]
public sealed class DemoResetTests(SuppliersApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Demo_reset_clears_business_rows_and_reseeds()
    {
        var name = $"reset_{Guid.NewGuid():N}"[..20];
        await using var factory = new SuppliersApiFactory(
            app.Infra, app.Access, new MongoCommandLog(), app.Infra.ConnectionStringFor(name),
            new Dictionary<string, string?> { ["DemoReset:Enabled"] = "true" });
        using var client = factory.CreateClient();
        var suppliers = app.Infra.Client.GetDatabase(name).GetCollection<BsonDocument>(DependencyInjection.SuppliersCollection);
        var seeded = DemoSeeder.DemoSuppliers().Select(s => s.Id).ToList();

        // On an empty database the reset seeds the catalogs.
        (await DemoResetProbe.ResetAsync(client, "suppliers")).ShouldBe(HttpStatusCode.NoContent);
        (await IdsAsync(suppliers)).ShouldBe(seeded, ignoreOrder: true);

        // A reviewer's changes: a supplier of another tenant, and a renamed demo supplier.
        await suppliers.InsertOneAsync(
            new BsonDocument { ["_id"] = "zz:extra", ["TenantId"] = "zz", ["Code"] = "extra", ["Name"] = "Extra", ["Active"] = true, ["Items"] = new BsonArray() },
            cancellationToken: Ct);
        await suppliers.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", seeded[0]), Builders<BsonDocument>.Update.Set("Name", "Renamed"), cancellationToken: Ct);

        using var changes = await suppliers.WatchAsync(
            new ChangeStreamOptions { FullDocument = ChangeStreamFullDocumentOption.Default }, Ct);
        (await DemoResetProbe.ResetAsync(client, "suppliers")).ShouldBe(HttpStatusCode.NoContent);

        (await IdsAsync(suppliers)).ShouldBe(seeded, ignoreOrder: true);
        var renamed = await suppliers.Find(Builders<BsonDocument>.Filter.Eq("_id", seeded[0])).SingleAsync(Ct);
        renamed["Name"].AsString.ShouldBe(DemoSeeder.DemoSuppliers().First().Name);

        // Every delete came right after the app's "deleting" stamp on the same document (ruling 5, plan 06 ruling 16).
        var seen = await ReadAsync(changes, until: list => list.Count(c => c.OperationType == ChangeStreamOperationType.Delete) >= seeded.Count + 1);
        var stamped = new HashSet<string>();
        foreach (var change in seen)
        {
            var id = change.DocumentKey["_id"].AsString;
            if (change.OperationType == ChangeStreamOperationType.Update
                && change.UpdateDescription.UpdatedFields.TryGetValue("_audit", out var stamp)
                && stamp.AsBsonDocument["deleting"].AsBoolean)
            {
                stamped.Add(id);
            }
            else if (change.OperationType == ChangeStreamOperationType.Delete)
            {
                stamped.ShouldContain(id, $"the delete of {id} was not stamped first");
            }
        }

        (await DemoResetProbe.ResetAsync(client, "suppliers")).ShouldBe(HttpStatusCode.NoContent, "twice is safe");
        (await IdsAsync(suppliers)).ShouldBe(seeded, ignoreOrder: true);
    }

    private static async Task<List<string>> IdsAsync(IMongoCollection<BsonDocument> suppliers) =>
        (await suppliers.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct)).Select(d => d["_id"].AsString).ToList();

    private static async Task<List<ChangeStreamDocument<BsonDocument>>> ReadAsync(
        IChangeStreamCursor<ChangeStreamDocument<BsonDocument>> cursor, Func<List<ChangeStreamDocument<BsonDocument>>, bool> until)
    {
        var seen = new List<ChangeStreamDocument<BsonDocument>>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!until(seen) && DateTime.UtcNow < deadline && await cursor.MoveNextAsync(Ct))
        {
            seen.AddRange(cursor.Current);
        }

        until(seen).ShouldBeTrue($"saw {seen.Count} changes");
        return seen;
    }
}
