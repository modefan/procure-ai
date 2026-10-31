using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Mongo;

/// <summary>
/// <see cref="TenantCollection{T}"/> against a real single-node replica set (the same shape as the AppHost's MongoDB): a service
/// can only reach its caller's tenant's documents, whatever filter it passes.
/// </summary>
public sealed class TenantCollectionTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Note : ITenantDocument
    {
        [BsonId]
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        public string TenantId { get; init; } = null!;

        public string Text { get; init; } = null!;
    }

    private sealed class Caller(string? tenantId) : ICallerContext
    {
        public string UserId => "u-1";

        public string ActorId => "u-1";

        public string? TenantId => tenantId;

        public string? CallingService => null;

        public CallerKind Kind => CallerKind.User;
    }

    private IMongoCollection<Note> Raw(string name) => mongo.Client.GetDatabase("tenant-tests").GetCollection<Note>(name);

    private static TenantCollection<Note> For(IMongoCollection<Note> raw, string? tenant) => new(raw, new Caller(tenant));

    private async Task<IMongoCollection<Note>> SeededAsync()
    {
        var raw = Raw($"notes-{Guid.NewGuid():N}");
        await raw.InsertManyAsync(
        [
            new Note { Id = "a1", TenantId = "A", Text = "alpha" },
            new Note { Id = "a2", TenantId = "A", Text = "alpha two" },
            new Note { Id = "b1", TenantId = "B", Text = "beta" },
        ], cancellationToken: Ct);
        return raw;
    }

    [Fact]
    public async Task Find_returns_only_the_callers_tenant()
    {
        var raw = await SeededAsync();

        // Even a filter that asks for everything, or for another tenant's id, cannot leave the caller's tenant.
        (await For(raw, "A").Find(Builders<Note>.Filter.Empty).ToListAsync(Ct)).Select(n => n.Id).ShouldBe(["a1", "a2"], ignoreOrder: true);
        (await For(raw, "A").Find(n => n.Id == "b1").ToListAsync(Ct)).ShouldBeEmpty();
        (await For(raw, "A").Find(Builders<Note>.Filter.Eq(n => n.TenantId, "B")).ToListAsync(Ct)).ShouldBeEmpty();
        // An OR cannot widen it either: the tenant filter is ANDed around the caller's filter.
        var either = Builders<Note>.Filter.Or(Builders<Note>.Filter.Eq(n => n.Id, "b1"), Builders<Note>.Filter.Eq(n => n.Id, "a1"));
        (await For(raw, "A").Find(either).ToListAsync(Ct)).Select(n => n.Id).ShouldBe(["a1"]);
        (await For(raw, "A").CountAsync(Builders<Note>.Filter.Empty, Ct)).ShouldBe(2);
        (await For(raw, "B").CountAsync(Builders<Note>.Filter.Empty, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Replace_of_another_tenants_document_matches_nothing()
    {
        var raw = await SeededAsync();

        var result = await For(raw, "A").ReplaceOneAsync(n => n.Id == "b1", new Note { Id = "b1", TenantId = "A", Text = "taken over" }, cancellationToken: Ct);

        result.MatchedCount.ShouldBe(0);
        var b1 = await raw.Find(n => n.Id == "b1").SingleAsync(Ct);
        b1.TenantId.ShouldBe("B");
        b1.Text.ShouldBe("beta");

        // Its own document is replaced.
        (await For(raw, "A").ReplaceOneAsync(n => n.Id == "a1", new Note { Id = "a1", TenantId = "A", Text = "changed" }, cancellationToken: Ct)).MatchedCount.ShouldBe(1);
        (await raw.Find(n => n.Id == "a1").SingleAsync(Ct)).Text.ShouldBe("changed");

        // Replacing with a document of another tenant is refused, not silently moved.
        await Should.ThrowAsync<ForbiddenException>(() =>
            For(raw, "A").ReplaceOneAsync(n => n.Id == "a2", new Note { Id = "a2", TenantId = "B", Text = "moved" }, cancellationToken: Ct));
        (await raw.Find(n => n.Id == "a2").SingleAsync(Ct)).TenantId.ShouldBe("A");
    }

    [Fact]
    public async Task Delete_removes_only_the_callers_tenants_documents()
    {
        var raw = await SeededAsync();

        (await For(raw, "A").DeleteOneAsync(n => n.Id == "b1", Ct)).DeletedCount.ShouldBe(0);
        (await For(raw, "A").DeleteOneAsync(n => n.Id == "a1", Ct)).DeletedCount.ShouldBe(1);
        (await raw.CountDocumentsAsync(Builders<Note>.Filter.Empty, cancellationToken: Ct)).ShouldBe(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task No_tenant_throws_forbidden(string? tenant)
    {
        var raw = await SeededAsync();
        var collection = For(raw, tenant);

        await Should.ThrowAsync<ForbiddenException>(() => collection.Find(Builders<Note>.Filter.Empty).ToListAsync(Ct));
        await Should.ThrowAsync<ForbiddenException>(() => collection.CountAsync(Builders<Note>.Filter.Empty, Ct));
        await Should.ThrowAsync<ForbiddenException>(() => collection.InsertOneAsync(new Note { TenantId = "A", Text = "x" }, Ct));
        await Should.ThrowAsync<ForbiddenException>(() => collection.ReplaceOneAsync(n => n.Id == "a1", new Note { Id = "a1", TenantId = "A", Text = "x" }, cancellationToken: Ct));
        await Should.ThrowAsync<ForbiddenException>(() => collection.DeleteOneAsync(n => n.Id == "a1", Ct));
    }

    [Fact]
    public async Task Insert_with_other_tenant_id_throws()
    {
        var raw = await SeededAsync();
        var collection = For(raw, "A");

        await Should.ThrowAsync<ForbiddenException>(() => collection.InsertOneAsync(new Note { Id = "x1", TenantId = "B", Text = "smuggled" }, Ct));
        await Should.ThrowAsync<ForbiddenException>(() => collection.InsertManyAsync(
            [new Note { Id = "x2", TenantId = "A", Text = "fine" }, new Note { Id = "x3", TenantId = "B", Text = "smuggled" }], Ct));
        // Nothing of a refused batch was written.
        (await raw.CountDocumentsAsync(n => n.Id == "x2" || n.Id == "x1" || n.Id == "x3", cancellationToken: Ct)).ShouldBe(0);

        await collection.InsertOneAsync(new Note { Id = "x4", TenantId = "A", Text = "mine" }, Ct);
        (await raw.Find(n => n.Id == "x4").SingleAsync(Ct)).TenantId.ShouldBe("A");
    }

    [Fact]
    public async Task The_readiness_check_pings_the_server()
    {
        var healthy = await new MongoReadinessCheck(mongo.Client).CheckHealthAsync(new HealthCheckContext(), Ct);
        healthy.Status.ShouldBe(HealthStatus.Healthy);

        var unreachable = new MongoClient(new MongoClientSettings
        {
            Server = new MongoServerAddress("127.0.0.1", 1),
            ServerSelectionTimeout = TimeSpan.FromMilliseconds(300),
        });
        (await new MongoReadinessCheck(unreachable).CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Driver_commands_are_traced_under_the_registered_activity_source()
    {
        // AddProcureAiMongo registers MongoTelemetry.ActivitySourceName; this proves the driver emits spans on its own (no extra package).
        var started = new List<string>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == MongoTelemetry.ActivitySourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStarted = activity => { lock (started) { started.Add(activity.OperationName); } },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        await mongo.Client.GetDatabase("trace-tests").GetCollection<Note>("traced").Find(Builders<Note>.Filter.Empty).ToListAsync(Ct);

        lock (started)
        {
            started.ShouldNotBeEmpty("the MongoDB driver started no activity");
        }
    }

    private sealed class AuditedNote : ITenantDocument, IAuditedDocument
    {
        [BsonId]
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        public string TenantId { get; init; } = null!;

        public string Text { get; set; } = null!;

        [BsonElement(AuditStamp.ElementName)]
        [BsonIgnoreIfNull]
        public AuditStamp? Audit { get; set; }
    }

    /// <summary>Every insert and replace of an audited document carries the caller's stamp (plan 06 ruling 16): what change data capture attributes.</summary>
    [Fact]
    public async Task Insert_and_replace_stamp_an_audited_document_with_the_caller()
    {
        var raw = mongo.Client.GetDatabase("tenant-tests").GetCollection<AuditedNote>($"audited-{Guid.NewGuid():N}");
        var collection = new TenantCollection<AuditedNote>(raw, new Caller("A"));
        var before = DateTime.UtcNow.AddSeconds(-1);

        await collection.InsertOneAsync(new AuditedNote { Id = "n1", TenantId = "A", Text = "first" }, Ct);
        var inserted = await raw.Find(n => n.Id == "n1").SingleAsync(Ct);
        await collection.ReplaceOneAsync(n => n.Id == "n1", new AuditedNote { Id = "n1", TenantId = "A", Text = "second" }, cancellationToken: Ct);
        var replaced = await raw.Find(n => n.Id == "n1").SingleAsync(Ct);

        foreach (var stamp in new[] { inserted.Audit, replaced.Audit })
        {
            stamp.ShouldNotBeNull();
            stamp.UserId.ShouldBe("u-1");
            stamp.At.ShouldBeGreaterThan(before);
            stamp.Deleting.ShouldBeFalse();
        }

        replaced.Audit!.At.ShouldBeGreaterThanOrEqualTo(inserted.Audit!.At);
    }

    /// <summary>A delete event carries no document: the document is stamped "deleting" first, so Audit can tell the app's delete.</summary>
    [Fact]
    public async Task Delete_stamps_an_audited_document_as_deleting_before_it_goes()
    {
        var raw = mongo.Client.GetDatabase("tenant-tests").GetCollection<AuditedNote>($"audited-{Guid.NewGuid():N}");
        var collection = new TenantCollection<AuditedNote>(raw, new Caller("A"));
        await collection.InsertOneAsync(new AuditedNote { Id = "n1", TenantId = "A", Text = "first" }, Ct);
        using var changes = await raw.WatchAsync(
            new ChangeStreamOptions { FullDocument = ChangeStreamFullDocumentOption.UpdateLookup }, Ct);

        await collection.DeleteOneAsync(n => n.Id == "n1", Ct);

        var events = new List<ChangeStreamDocument<AuditedNote>>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (events.Count < 2 && !timeout.IsCancellationRequested)
        {
            try
            {
                if (await changes.MoveNextAsync(timeout.Token))
                {
                    events.AddRange(changes.Current);
                }
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                break;
            }
        }

        events.Select(e => e.OperationType).ShouldBe([ChangeStreamOperationType.Update, ChangeStreamOperationType.Delete]);
        var stamp = events[0].UpdateDescription.UpdatedFields[AuditStamp.ElementName].AsBsonDocument;
        stamp["userId"].AsString.ShouldBe("u-1");
        stamp["deleting"].AsBoolean.ShouldBeTrue();
        (await raw.CountDocumentsAsync(Builders<AuditedNote>.Filter.Empty, cancellationToken: Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Migrations_receive_the_database()
    {
        var database = mongo.Client.GetDatabase("migration-tests");

        await new IndexMigration().ApplyAsync(database, Ct);

        var indexes = await (await database.GetCollection<Note>("indexed").Indexes.ListAsync(Ct)).ToListAsync(Ct);
        indexes.Select(i => i["name"].AsString).ShouldContain("tenant_text");
    }

    private sealed class IndexMigration : IMongoMigration
    {
        public Task ApplyAsync(IMongoDatabase database, CancellationToken cancellationToken) =>
            database.GetCollection<Note>("indexed").Indexes.CreateOneAsync(
                new CreateIndexModel<Note>(
                    Builders<Note>.IndexKeys.Ascending(n => n.TenantId).Ascending(n => n.Text),
                    new CreateIndexOptions { Name = "tenant_text" }),
                cancellationToken: cancellationToken);
    }
}
