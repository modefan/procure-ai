using Marten;
using Marten.Services;
using Npgsql;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application.Status;
using Shouldly;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>Audit's part of the system status snapshot (plan 06 ruling 10): the open outside-the-app alerts, per tenant, newest first.</summary>
[Collection(AuditCollection.Name)]
public sealed class StatusContributorTests(AuditApp app)
{
    [Fact]
    public async Task Audit_lists_open_outside_app_alerts()
    {
        var acme = AuditApp.NewTenant();
        var globex = AuditApp.NewTenant();
        var now = DateTimeOffset.UtcNow;
        var store = app.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            // 22 open alerts of Acme (only the newest 20 are listed), one withdrawn (the app's row came late), one of Globex.
            for (var i = 0; i < 22; i++)
            {
                session.Store(Alert(acme, $"purchase_orders-{i}", now.AddMinutes(-i)));
            }

            session.Store(Alert(acme, "withdrawn", now.AddMinutes(1)) with { WithdrawnAt = now.AddMinutes(2) });
            session.Store(Alert(globex, "globex-row", now));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = app.Services.CreateAsyncScope();
        var contributor = scope.ServiceProvider.GetServices<IStatusContributor>().Single(c => c.Name == "audit");
        var section = await contributor.CollectAsync(TestContext.Current.CancellationToken);

        section.Counters[StatusCounters.Key(acme, StatusCounters.OutsideAppAlertsOpen)].ShouldBe(22, "a withdrawn alert is not open");
        section.Counters[StatusCounters.Key(globex, StatusCounters.OutsideAppAlertsOpen)].ShouldBe(1);
        var acmeItems = section.Items.Where(i => i.TenantId == acme).ToList();
        acmeItems.Count.ShouldBe(20, "the newest 20 per tenant");
        acmeItems.Select(i => i.At).ShouldBeInOrder(SortDirection.Descending);
        acmeItems[0].At.ShouldBe(now, TimeSpan.FromMilliseconds(1));
        acmeItems[0].Kind.ShouldBe("outside-app-change");
        acmeItems[0].Text.ShouldContain("purchase_orders");
        acmeItems[0].Link.ShouldBe("/app/admin/data-changes?outside=true&table=purchase_orders");
        acmeItems.ShouldNotContain(i => i.Text.Contains("withdrawn"));
        section.Items.Where(i => i.TenantId == globex).ShouldHaveSingleItem().Text.ShouldContain("globex-row");
    }

    [Fact]
    public async Task Open_alert_counts_are_grouped_in_sql()
    {
        var tenant = AuditApp.NewTenant();
        var store = app.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Store(Alert(tenant, "grouped-1", DateTimeOffset.UtcNow));
            session.Store(Alert(tenant, "grouped-2", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var log = new SqlLog();
        var original = store.Options.Logger();
        ((StoreOptions)store.Options).Logger(log);
        OpenOutsideAppAlerts open;
        try
        {
            open = await new MartenDataChangeStore(store, TimeProvider.System).OpenOutsideAppAlertsAsync(1, TestContext.Current.CancellationToken);
        }
        finally
        {
            ((StoreOptions)store.Options).Logger(original);
        }

        open.Counts.Single(c => c.TenantId == tenant).Count.ShouldBe(2);
        // Background Marten work on the shared store can log commands too: look only at those on the alert table.
        var alertCommands = log.Commands.Where(c => c.Contains("mt_doc_outsideappalert", StringComparison.OrdinalIgnoreCase)).ToList();
        // The counts come from one GROUP BY command; the alerts themselves are only read for the newest per tenant (Take).
        var groupBy = alertCommands.Where(c => c.Contains("group by", StringComparison.OrdinalIgnoreCase)).ToList();
        groupBy.ShouldHaveSingleItem();
        groupBy[0].ShouldContain("count(", Case.Insensitive);
        alertCommands.Where(c => !c.Contains("group by", StringComparison.OrdinalIgnoreCase))
            .ShouldAllBe(c => c.Contains("limit", StringComparison.OrdinalIgnoreCase), "no unbounded read of the alerts");
    }

    [Fact]
    public async Task Connection_string_handed_to_Marten_carries_the_max_pool_size()
    {
        // The test database's own string says nothing about pooling (Npgsql's default is 100); ServiceHost adds Database:MaxPoolSize (default 10).
        var store = app.Services.GetRequiredService<IDocumentStore>();
        await using var connection = store.Storage.Database.CreateConnection();
        new NpgsqlConnectionStringBuilder(connection.ConnectionString).MaxPoolSize
            .ShouldBe(new ProcureAi.BuildingBlocks.Web.Options.DatabaseOptions().MaxPoolSize);
    }

    private static OutsideAppAlert Alert(string tenant, string key, DateTimeOffset committedAt) =>
        new($"test-{Guid.NewGuid():N}", "purchasingdb", "purchase_orders", key, tenant, 4711, committedAt, committedAt, null);

    /// <summary>Records the text of every command Marten runs while it is the store's logger.</summary>
    private sealed class SqlLog : IMartenLogger, IMartenSessionLogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _commands = new();

        public IReadOnlyList<string> Commands => [.. _commands];

        public IMartenSessionLogger StartSession(IQuerySession session) => this;

        public void SchemaChange(string sql) { }

        public void LogSuccess(NpgsqlCommand command) => _commands.Enqueue(command.CommandText);

        public void LogSuccess(NpgsqlBatch batch)
        {
            foreach (var c in batch.BatchCommands)
            {
                _commands.Enqueue(c.CommandText);
            }
        }

        public void LogFailure(NpgsqlCommand command, Exception ex) { }

        public void LogFailure(NpgsqlBatch batch, Exception ex) { }

        public void LogFailure(Exception ex, string message) { }

        public void RecordSavedChanges(IDocumentSession session, IChangeSet commit) { }

        public void OnBeforeExecute(NpgsqlCommand command) { }

        public void OnBeforeExecute(NpgsqlBatch batch) { }
    }
}
