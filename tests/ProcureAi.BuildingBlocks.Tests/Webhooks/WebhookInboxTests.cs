using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Persistence;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Webhooks;

public sealed class WebhookInboxTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed class InboxDbContext(DbContextOptions<InboxDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddProcessedWebhooks();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InboxDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connectionString).Options);

    private async Task<string> NewDatabaseAsync(string name)
    {
        var connectionString = await postgres.CreateDatabaseAsync(name);
        await using var db = NewContext(connectionString);
        await db.Database.EnsureCreatedAsync(Ct);
        return connectionString;
    }

    [Fact]
    public async Task First_record_is_true_and_a_committed_duplicate_is_false()
    {
        var connectionString = await NewDatabaseAsync("webhook_inbox_basic");
        await using (var first = NewContext(connectionString))
        {
            (await new WebhookInbox<InboxDbContext>(first).TryRecordAsync("supplier", "e1", Ct)).ShouldBeTrue();
            await first.SaveChangesAsync(Ct);
        }

        await using var second = NewContext(connectionString);
        var inbox = new WebhookInbox<InboxDbContext>(second);
        (await inbox.TryRecordAsync("supplier", "e1", Ct)).ShouldBeFalse();
        (await inbox.TryRecordAsync("supplier", "e2", Ct)).ShouldBeTrue();
        (await inbox.TryRecordAsync("other", "e1", Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Recording_twice_in_one_unit_of_work_is_a_duplicate()
    {
        var connectionString = await NewDatabaseAsync("webhook_inbox_local");
        await using var db = NewContext(connectionString);
        var inbox = new WebhookInbox<InboxDbContext>(db);

        (await inbox.TryRecordAsync("supplier", "e1", Ct)).ShouldBeTrue();
        (await inbox.TryRecordAsync("supplier", "e1", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unsaved_record_is_gone_with_the_unit_of_work()
    {
        var connectionString = await NewDatabaseAsync("webhook_inbox_rollback");
        await using (var abandoned = NewContext(connectionString))
        {
            (await new WebhookInbox<InboxDbContext>(abandoned).TryRecordAsync("supplier", "e1", Ct)).ShouldBeTrue();
        }

        await using var next = NewContext(connectionString);
        (await new WebhookInbox<InboxDbContext>(next).TryRecordAsync("supplier", "e1", Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Two_contexts_recording_the_same_event_at_once_commit_exactly_one()
    {
        var connectionString = await NewDatabaseAsync("webhook_inbox_race");
        await using var left = NewContext(connectionString);
        await using var right = NewContext(connectionString);
        // Both check before either commits, as two concurrent deliveries would.
        (await new WebhookInbox<InboxDbContext>(left).TryRecordAsync("supplier", "e1", Ct)).ShouldBeTrue();
        (await new WebhookInbox<InboxDbContext>(right).TryRecordAsync("supplier", "e1", Ct)).ShouldBeTrue();

        await left.SaveChangesAsync(Ct);
        var failure = await Should.ThrowAsync<DbUpdateException>(() => right.SaveChangesAsync(Ct));

        WebhookInbox.IsDuplicate(failure).ShouldBeTrue();
        await using var check = NewContext(connectionString);
        (await check.Set<ProcessedWebhook>().CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ApplyOnce_treats_a_concurrent_duplicate_as_applied_and_lets_other_failures_through()
    {
        var connectionString = await NewDatabaseAsync("webhook_inbox_apply_once");
        await using var left = NewContext(connectionString);
        await using var right = NewContext(connectionString);
        (await new WebhookInbox<InboxDbContext>(left).TryRecordAsync("supplier", "e1", Ct)).ShouldBeTrue();
        (await new WebhookInbox<InboxDbContext>(right).TryRecordAsync("supplier", "e1", Ct)).ShouldBeTrue();

        (await WebhookInbox.ApplyOnceAsync(() => left.SaveChangesAsync(Ct))).ShouldBeTrue("the first delivery applies it");
        (await WebhookInbox.ApplyOnceAsync(() => right.SaveChangesAsync(Ct))).ShouldBeFalse("the concurrent duplicate collided on the inbox key");
        await Should.ThrowAsync<InvalidOperationException>(() => WebhookInbox.ApplyOnceAsync(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task Other_database_failures_are_not_duplicates()
    {
        var connectionString = await NewDatabaseAsync("webhook_inbox_other");
        await using var db = NewContext(connectionString);
        db.Set<ProcessedWebhook>().Add(new ProcessedWebhook { Source = new string('s', 101), EventId = "e1" });

        var failure = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        WebhookInbox.IsDuplicate(failure).ShouldBeFalse();
    }
}
