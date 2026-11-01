using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using ProcureAi.Assistant.Infrastructure.Persistence;
using Shouldly;
using StackExchange.Redis;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// The nightly demo reset of the Assistant (plan 06 rulings 4 and 5) on databases of its own: policy documents, files, chunks and past
/// purchases (Postgres, ordinary deletes the interceptor records), conversations and invoice scans (MongoDB, through TenantCollection) and
/// the question quotas go; the procurement policy is seeded again and ingested.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class DemoResetTests(AssistantApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Demo_reset_clears_business_rows_and_reseeds()
    {
        var name = $"reset_{Guid.NewGuid():N}"[..20];
        var databases = await app.Infra.CreateSeededDatabasesAsync(name);
        await using var factory = app.CreateInstance(databases, new Dictionary<string, string?> { ["DemoReset:Enabled"] = "true" });
        using var client = factory.CreateClient();
        var chat = new MongoClient(databases.Chat).GetDatabase(name + "_chat");
        await WaitForAsync(() => CountAsync(databases.Database, ReadyDocuments), n => n == DemoSeeder.Documents.Count, "the seeded policy ingested");
        var seededChunks = await CountAsync(databases.Database, "select count(*) from chunks");

        foreach (var collection in new[] { "conversations", "invoice_scans" })
        {
            await chat.GetCollection<BsonDocument>(collection).InsertManyAsync(
                [
                    new BsonDocument { ["_id"] = Guid.NewGuid().ToString(), ["TenantId"] = "acme", ["UserId"] = "tom" },
                    new BsonDocument { ["_id"] = Guid.NewGuid().ToString(), ["TenantId"] = "globex", ["UserId"] = "ben" },
                ],
                cancellationToken: Ct);
        }

        var redis = await ConnectionMultiplexer.ConnectAsync(app.Infra.RedisConnectionString);
        await using var _ = redis;
        await redis.GetDatabase().StringSetAsync("assistant:quota:acme:tom:2026-10-31", 5);

        (await DemoResetProbe.ResetAsync(client, "assistant")).ShouldBe(HttpStatusCode.NoContent);

        (await chat.GetCollection<BsonDocument>("conversations").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).ShouldBe(0);
        (await chat.GetCollection<BsonDocument>("invoice_scans").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).ShouldBe(0);
        (await redis.GetDatabase().KeyExistsAsync("assistant:quota:acme:tom:2026-10-31")).ShouldBeFalse();
        // The seeded documents were deleted (one interceptor row each) and seeded again under their fixed IDs, then ingested again.
        (await DemoResetProbe.DeleteEntriesAsync(databases.Database, "policy_documents")).Select(d => d.Key)
            .ShouldBe(DemoSeeder.Documents.Select(d => d.DocumentId.ToString()), ignoreOrder: true);
        (await CountAsync(databases.Database, "select count(*) from policy_documents")).ShouldBe(DemoSeeder.Documents.Count);
        await WaitForAsync(() => CountAsync(databases.Database, ReadyDocuments), n => n == DemoSeeder.Documents.Count, "the policy ingested again");
        (await CountAsync(databases.Database, "select count(*) from chunks")).ShouldBe(seededChunks);

        (await DemoResetProbe.ResetAsync(client, "assistant")).ShouldBe(HttpStatusCode.NoContent, "twice is safe");
    }

    /// <summary>
    /// The service's own <c>audit_entries</c> are demo audit data too (spec 5.1, plan 07 Task 0b): the reset empties them first, so afterwards
    /// the table holds only the reset's own Delete rows (ruling 5) and what came after.
    /// </summary>
    [Fact]
    public async Task Demo_reset_clears_audit_entries()
    {
        var name = $"reset_{Guid.NewGuid():N}"[..20];
        var databases = await app.Infra.CreateSeededDatabasesAsync(name);
        await using var factory = app.CreateInstance(databases, new Dictionary<string, string?> { ["DemoReset:Enabled"] = "true" });
        using var client = factory.CreateClient();
        await WaitForAsync(() => CountAsync(databases.Database, ReadyDocuments), n => n == DemoSeeder.Documents.Count, "the seeded policy ingested");
        var earlier = await DemoResetProbe.InsertEarlierAuditEntryAsync(databases.Database);
        var started = DateTimeOffset.UtcNow;

        (await DemoResetProbe.ResetAsync(client, "assistant")).ShouldBe(HttpStatusCode.NoContent);

        (await DemoResetProbe.CountAsync(databases.Database, $"select count(*) from audit_entries where id = '{earlier}'")).ShouldBe(0, "yesterday's interceptor row");
        (await DemoResetProbe.AuditEntriesBeforeAsync(databases.Database, started)).ShouldBe(0, "every audit_entries row from before the reset is gone (carry-over 5)");
        (await DemoResetProbe.DeleteEntriesAsync(databases.Database, "policy_documents")).ShouldNotBeEmpty("the reset's own Delete rows stay");
    }

    private const string ReadyDocuments = "select count(*) from policy_documents where status = 'Ready'";

    private static Task<long> CountAsync(string database, string sql) => DemoResetProbe.CountAsync(database, sql);

    private static async Task WaitForAsync(Func<Task<long>> read, Func<long, bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        long value;
        while (!done(value = await read()))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{what}: still {value}");
            }

            await Task.Delay(250, Ct);
        }
    }
}
