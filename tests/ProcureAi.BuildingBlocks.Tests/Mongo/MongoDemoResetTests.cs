using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Mongo;

/// <summary>
/// Final review F14: the demo reset deletes tenant by tenant, so a document without a tenant cannot be deleted through a
/// <see cref="TenantCollection{T}"/>. It stays, and the reset says how many it skipped instead of leaving them silently.
/// </summary>
public sealed class MongoDemoResetTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Documents_without_a_tenant_are_skipped_and_counted_in_the_log()
    {
        var raw = mongo.Client.GetDatabase("tenant-tests").GetCollection<Note>($"reset-{Guid.NewGuid():N}");
        await raw.InsertManyAsync(
        [
            new Note { Id = "a1", TenantId = "A" },
            new Note { Id = "b1", TenantId = "B" },
            new Note { Id = "none", TenantId = null! },
            new Note { Id = "empty", TenantId = "" },
        ], cancellationToken: Ct);
        var logger = new ListLogger();

        var deleted = await MongoDemoReset.DeleteAllAsync(raw, logger, cancellationToken: Ct);

        deleted.ShouldBe(2);
        (await raw.Find(FilterDefinition<Note>.Empty).ToListAsync(Ct)).Select(n => n.Id).Order().ShouldBe(["empty", "none"]);
        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("2 documents");
        warning.Message.ShouldContain(raw.CollectionNamespace.CollectionName);
    }

    [Fact]
    public async Task Nothing_skipped_logs_nothing()
    {
        var raw = mongo.Client.GetDatabase("tenant-tests").GetCollection<Note>($"reset-{Guid.NewGuid():N}");
        await raw.InsertOneAsync(new Note { Id = "a1", TenantId = "A" }, cancellationToken: Ct);
        var logger = new ListLogger();

        (await MongoDemoReset.DeleteAllAsync(raw, logger, cancellationToken: Ct)).ShouldBe(1);

        logger.Entries.ShouldBeEmpty();
    }

    private sealed class Note : ITenantDocument
    {
        [BsonId]
        public string Id { get; init; } = null!;

        public string TenantId { get; init; } = null!;
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
