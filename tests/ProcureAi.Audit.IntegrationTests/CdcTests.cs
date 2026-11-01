using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using ProcureAi.Audit.Application;
using ProcureAi.Audit.Application.Cdc;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.Audit.Domain;
using ProcureAi.Budgets.Domain;
using ProcureAi.Budgets.Infrastructure;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts;
using Shouldly;
using Wolverine;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// The data change audit: Debezium's changes of the other services' databases, matched to the rows their audit interceptor wrote
/// in the same transaction (the app user), or flagged "changed outside the app"; retention; and the two data-change endpoints.
/// </summary>
[Collection(AuditCollection.Name)]
public sealed class CdcTests(AuditApp app)
{
    private const string Tom = DemoUsers.Tom;
    private const string OutsideAppMetric = "procure.audit.outside_app_changes";
    private const string Requests = "/api/v1/requests";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Long enough for a change to pass the grace period and be resolved.</summary>
    private static TimeSpan AfterGrace => AuditApiFactory.Grace + AuditApp.Timeout;

    [Fact]
    public async Task App_change_is_attributed_to_user_and_actor()
    {
        await app.EnsureCdcAsync();
        var tenant = AuditApp.NewTenant();

        // The admin key acting for tom: tom is the user, claude-admin the actor (what the interceptor wrote).
        var (id, version) = await CreateRequestAsync(tenant);
        await SubmitAsync(id, version, tenant);

        var changes = await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Database == AuditApp.RequestsDb && c.Table == "purchase_requests" && c.PrimaryKey == id.ToString()),
            found => found.Any(c => c.Operation == "Insert") && found.Any(c => c.Operation == "Update")
                && found.All(c => c.Attribution.Kind == AttributionKind.App));
        changes.ShouldAllBe(c => c.Attribution.UserId == Tom && c.Attribution.ActorId == "claude-admin");
        changes.ShouldAllBe(c => c.TenantId == tenant && c.SubjectId == id);

        // Each attribution names the interceptor row of the same transaction.
        foreach (var change in changes)
        {
            var entryId = change.Attribution.InterceptorEntryId.ShouldNotBeNull();
            var entry = (await app.QueryAsync<InterceptorEntry>(q => q.Where(e => e.Id == entryId))).ShouldHaveSingleItem();
            change.TransactionId.ShouldBe(entry.TransactionId);
            entry.PrimaryKey.ShouldBe(change.PrimaryKey);
        }

        // The lines have no tenant column: the tenant comes from the matched interceptor row.
        var lines = await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Database == AuditApp.RequestsDb && c.Table == "request_lines" && c.SubjectId == id),
            found => found.Count > 0 && found.All(c => c.Attribution.Kind == AttributionKind.App));
        lines.ShouldAllBe(c => c.TenantId == tenant && c.Attribution.UserId == Tom && c.PrimaryKey == $"{id},1");
    }

    [Fact]
    public async Task Direct_sql_update_is_flagged_outside_app()
    {
        await app.EnsureCdcAsync();
        using var metric = new CounterRecorder(OutsideAppMetric);
        var tenant = AuditApp.NewTenant();
        var (id, _) = await CreateRequestAsync(tenant);
        await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Table == "purchase_requests" && c.PrimaryKey == id.ToString()),
            found => found.Count == 1 && found[0].Attribution.Kind == AttributionKind.App);

        const string handTyped = "Typed in psql 0451";
        await ExecuteAsync(app.RequestsDbConnectionString, "UPDATE purchase_requests SET title = @title WHERE id = @id", ("title", handTyped), ("id", id));

        var outside = (await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Table == "purchase_requests" && c.PrimaryKey == id.ToString() && c.Operation == "Update"),
            found => found.Count == 1 && found[0].Attribution.Kind == AttributionKind.OutsideApp,
            AfterGrace)).Single();
        outside.TenantId.ShouldBe(tenant);
        outside.SubjectId.ShouldBe(id);
        JsonDocument.Parse(outside.After!).RootElement.GetProperty("title").GetString().ShouldBe(handTyped);
        outside.Attribution.UserId.ShouldBeNull();

        var alert = (await app.QueryAsync<OutsideAppAlert>(q => q.Where(a => a.Id == outside.Id))).ShouldHaveSingleItem();
        alert.Database.ShouldBe(AuditApp.RequestsDb);
        alert.Table.ShouldBe("purchase_requests");
        alert.TenantId.ShouldBe(tenant);
        alert.WithdrawnAt.ShouldBeNull();
        metric.Sum(("database", AuditApp.RequestsDb), ("table", "purchase_requests")).ShouldBe(1);

        // The changed values never reach the logs.
        app.Logs.Events.ShouldNotContain(e =>
            e.RenderMessage(null).Contains(handTyped) || e.Properties.Values.Any(v => v.ToString().Contains(handTyped)));
    }

    [Fact]
    public async Task Interceptor_row_arriving_first_or_second_both_match()
    {
        var database = NewDatabase();
        var tenant = AuditApp.NewTenant();
        var entryFirst = SyntheticTransaction.New(database, tenant, DemoUsers.Maria, "claude-admin");
        var changeFirst = SyntheticTransaction.New(database, tenant);

        await PublishAsync(entryFirst.EntryRoutingKey, entryFirst.EntryJson());
        await app.WaitForAsync<InterceptorEntry>(q => q.Where(e => e.Id == entryFirst.EntryId), found => found.Count == 1);
        await PublishAsync(entryFirst.ChangeRoutingKey, entryFirst.ChangeJson());

        await PublishAsync(changeFirst.ChangeRoutingKey, changeFirst.ChangeJson());
        await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Id == changeFirst.ChangeId), found => found.Count == 1 && found[0].Attribution.Kind == AttributionKind.Pending);
        await PublishAsync(changeFirst.EntryRoutingKey, changeFirst.EntryJson());

        foreach (var (tx, user, actor) in new[] { (entryFirst, DemoUsers.Maria, "claude-admin"), (changeFirst, Tom, Tom) })
        {
            var change = (await app.WaitForAsync<DataChange>(
                q => q.Where(c => c.Id == tx.ChangeId), found => found.Count == 1 && found[0].Attribution.Kind == AttributionKind.App)).Single();
            change.Attribution.ShouldBe(new Attribution(AttributionKind.App, user, actor, null, $"corr-{tx.TransactionId}", tx.EntryId));
            change.SubjectId.ShouldBe(tx.RowId);
            change.TenantId.ShouldBe(tenant);
            change.Operation.ShouldBe("Update");
        }

        // The grace timeout of the change that waited does not undo its match.
        await Task.Delay(AuditApiFactory.Grace + TimeSpan.FromSeconds(2), Ct);
        (await app.QueryAsync<DataChange>(q => q.Where(c => c.Database == database))).ShouldAllBe(c => c.Attribution.Kind == AttributionKind.App);
        (await app.QueryAsync<OutsideAppAlert>(q => q.Where(a => a.Database == database))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_arrival_ends_in_exactly_one_app_attribution()
    {
        const int transactions = 24;
        var database = NewDatabase();
        var tenant = AuditApp.NewTenant();
        var txs = Enumerable.Range(0, transactions).Select(_ => SyntheticTransaction.New(database, tenant)).ToList();

        // Both rows of each transaction handled at the same moment, by the real handler (with its retry policy).
        await Task.WhenAll(txs.SelectMany(tx => new[]
        {
            Task.Run(() => InvokeAsync(tx.ChangeJson())),
            Task.Run(() => InvokeAsync(tx.EntryJson())),
        }));

        var changes = await app.QueryAsync<DataChange>(q => q.Where(c => c.Database == database));
        changes.Count.ShouldBe(transactions);
        foreach (var tx in txs)
        {
            var change = changes.Single(c => c.Id == tx.ChangeId);
            change.Attribution.Kind.ShouldBe(AttributionKind.App, $"transaction {tx.TransactionId} was not matched");
            change.Attribution.InterceptorEntryId.ShouldBe(tx.EntryId);
        }

        (await app.QueryAsync<InterceptorEntry>(q => q.Where(e => e.Database == database))).Count.ShouldBe(transactions);
    }

    [Fact]
    public async Task Grace_timeout_does_not_overwrite_a_completed_match()
    {
        using var metric = new CounterRecorder(OutsideAppMetric);
        var database = NewDatabase();
        var tenant = AuditApp.NewTenant();

        // Matched first, then the timeout fires: nothing changes, no alert, no metric.
        var matched = SyntheticTransaction.New(database, tenant);
        await InvokeAsync(matched.ChangeJson());
        await InvokeAsync(matched.EntryJson());
        await app.Bus.InvokeAsync(new ResolveAttribution(matched.ChangeId), Ct);

        var change = (await app.QueryAsync<DataChange>(q => q.Where(c => c.Id == matched.ChangeId))).ShouldHaveSingleItem();
        change.Attribution.Kind.ShouldBe(AttributionKind.App);
        (await app.QueryAsync<OutsideAppAlert>(q => q.Where(a => a.Id == matched.ChangeId))).ShouldBeEmpty();
        metric.Sum(("database", database)).ShouldBe(0);

        // The interceptor row and the timeout at the same moment: whichever wins, the change ends attributed to the app, and an
        // alert raised in between is withdrawn.
        var racing = Enumerable.Range(0, 16).Select(_ => SyntheticTransaction.New(database, tenant)).ToList();
        foreach (var tx in racing)
        {
            await InvokeAsync(tx.ChangeJson());
        }

        await Task.WhenAll(racing.SelectMany(tx => new[]
        {
            Task.Run(() => InvokeAsync(tx.EntryJson())),
            Task.Run(() => app.Bus.InvokeAsync(new ResolveAttribution(tx.ChangeId))),
        }));

        foreach (var tx in racing)
        {
            var raced = (await app.QueryAsync<DataChange>(q => q.Where(c => c.Id == tx.ChangeId))).ShouldHaveSingleItem();
            raced.Attribution.Kind.ShouldBe(AttributionKind.App);
            raced.Attribution.InterceptorEntryId.ShouldBe(tx.EntryId);
            (await app.QueryAsync<OutsideAppAlert>(q => q.Where(a => a.Id == tx.ChangeId))).ShouldAllBe(a => a.WithdrawnAt != null);
        }
    }

    [Fact]
    public async Task Interceptor_row_after_the_grace_decision_withdraws_the_alert()
    {
        using var metric = new CounterRecorder(OutsideAppMetric);
        var database = NewDatabase();
        var tx = SyntheticTransaction.New(database, AuditApp.NewTenant());

        // The change arrives, the grace period ends (decided outside the app, alert stored, metric counted), then its row arrives.
        await InvokeAsync(tx.ChangeJson());
        await app.Bus.InvokeAsync(new ResolveAttribution(tx.ChangeId), Ct);
        (await app.QueryAsync<DataChange>(q => q.Where(c => c.Id == tx.ChangeId))).ShouldHaveSingleItem()
            .Attribution.Kind.ShouldBe(AttributionKind.OutsideApp);
        (await app.QueryAsync<OutsideAppAlert>(q => q.Where(a => a.Id == tx.ChangeId))).ShouldHaveSingleItem().WithdrawnAt.ShouldBeNull();

        await InvokeAsync(tx.EntryJson());

        var change = (await app.QueryAsync<DataChange>(q => q.Where(c => c.Id == tx.ChangeId))).ShouldHaveSingleItem();
        change.Attribution.Kind.ShouldBe(AttributionKind.App);
        change.Attribution.InterceptorEntryId.ShouldBe(tx.EntryId);
        (await app.QueryAsync<OutsideAppAlert>(q => q.Where(a => a.Id == tx.ChangeId))).ShouldHaveSingleItem().WithdrawnAt.ShouldNotBeNull();
        metric.Sum(("database", database)).ShouldBe(1, "the decision was counted when made and is not undone");
    }

    [Fact]
    public async Task Row_changed_twice_in_one_transaction_pairs_each_change_with_its_own_row()
    {
        var database = NewDatabase();
        var first = SyntheticTransaction.New(database, AuditApp.NewTenant());
        var second = first with { ChangeLsn = first.ChangeLsn + 2, EntryLsn = first.EntryLsn + 2, EntryId = Guid.CreateVersion7() };

        // Log order: change 1, row 1, change 2, row 2.
        await InvokeAsync(first.ChangeJson());
        await InvokeAsync(first.EntryJson());
        await InvokeAsync(second.ChangeJson());
        var pending = (await app.QueryAsync<DataChange>(q => q.Where(c => c.Id == second.ChangeId))).ShouldHaveSingleItem();
        pending.Attribution.Kind.ShouldBe(AttributionKind.Pending, "its own row has not arrived; row 1 belongs to change 1");
        await InvokeAsync(second.EntryJson());

        (await app.QueryAsync<DataChange>(q => q.Where(c => c.Id == first.ChangeId))).ShouldHaveSingleItem()
            .Attribution.InterceptorEntryId.ShouldBe(first.EntryId);
        (await app.QueryAsync<DataChange>(q => q.Where(c => c.Id == second.ChangeId))).ShouldHaveSingleItem()
            .Attribution.InterceptorEntryId.ShouldBe(second.EntryId);
    }

    [Fact]
    public async Task Forged_interceptor_row_from_another_transaction_does_not_hide_the_change()
    {
        using var metric = new CounterRecorder(OutsideAppMetric);
        var database = NewDatabase();
        var tx = SyntheticTransaction.New(database, AuditApp.NewTenant());
        var forgedRowId = $"{database}:{tx.EntryLsn}:audit_entries:{tx.EntryId}";

        // The outside change, then a forged audit row copying its transaction_id but inserted by a transaction of its own.
        await InvokeAsync(tx.ChangeJson());
        await InvokeAsync(tx.ForgedEntryJson(tx.TransactionId + 1));

        await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Database == database),
            found => found.Count == 2 && found.All(c => c.Attribution.Kind == AttributionKind.OutsideApp), AfterGrace);
        var original = (await app.QueryAsync<DataChange>(q => q.Where(c => c.Id == tx.ChangeId))).ShouldHaveSingleItem();
        original.Attribution.InterceptorEntryId.ShouldBeNull();
        var forged = (await app.QueryAsync<DataChange>(q => q.Where(c => c.Id == forgedRowId))).ShouldHaveSingleItem();
        forged.Table.ShouldBe("audit_entries");
        forged.Attribution.Kind.ShouldBe(AttributionKind.OutsideApp);

        var alerts = await app.QueryAsync<OutsideAppAlert>(q => q.Where(a => a.Database == database));
        alerts.Count.ShouldBe(2);
        alerts.ShouldAllBe(a => a.WithdrawnAt == null);
        (await app.QueryAsync<InterceptorEntry>(q => q.Where(e => e.Database == database))).ShouldBeEmpty();
        metric.Sum(("database", database)).ShouldBe(2);
    }

    [Fact]
    public async Task Redelivered_change_is_stored_once()
    {
        await app.EnsureCdcAsync();
        var tenant = AuditApp.NewTenant();
        var probe = app.Probe;
        var rows = await probe.BindQueueAsync(ServiceInfraFixture.CdcExchange, $"{AuditApp.RequestsDb}.public.purchase_requests");
        var entries = await probe.BindQueueAsync(ServiceInfraFixture.CdcExchange, $"{AuditApp.RequestsDb}.public.audit_entries");

        var (id, _) = await CreateRequestAsync(tenant);
        var rawRow = await WaitForRawAboutAsync(probe, rows, id);
        var rawEntry = await WaitForRawAboutAsync(probe, entries, id);
        var first = (await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Table == "purchase_requests" && c.PrimaryKey == id.ToString()),
            found => found.Count == 1 && found[0].Attribution.Kind == AttributionKind.App)).Single();
        var handledBefore = await HandledCdcMessagesAsync();

        // Debezium after a restart (or the broker after a lost ack) delivers the same changes again, byte for byte.
        foreach (var raw in new[] { rawRow, rawEntry, rawRow, rawEntry })
        {
            await probe.RepublishAsync(ServiceInfraFixture.CdcExchange, raw, raw.RoutingKey);
        }

        await EventuallyAsync(async () => await HandledCdcMessagesAsync() >= handledBefore + 4);
        (await app.QueryAsync<DataChange>(q => q.Where(c => c.Table == "purchase_requests" && c.PrimaryKey == id.ToString())))
            .ShouldHaveSingleItem().ShouldBe(first);
        (await app.QueryAsync<InterceptorEntry>(q => q.Where(e => e.Id == first.Attribution.InterceptorEntryId))).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Wolverine_envelope_tables_produce_no_changes()
    {
        await app.EnsureCdcAsync();
        await using var cdc = await CdcListener.StartAsync(app.Infra.RabbitConnectionString);
        var tenant = AuditApp.NewTenant();

        // A submit writes the request and, in the same transaction, RequestSubmitted to Wolverine's outbox table.
        var (id, version) = await CreateRequestAsync(tenant);
        await SubmitAsync(id, version, tenant);
        await app.WaitForStoredAsync(tenant, stored => stored.Any(e => e.Type == "RequestSubmitted" && e.SubjectId == id));
        await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Table == "purchase_requests" && c.PrimaryKey == id.ToString() && c.Operation == "Update"),
            found => found.Count > 0);

        var tables = (await app.QueryAsync<DataChange>(q => q.Where(c => c.Database == AuditApp.RequestsDb))).Select(c => c.Table).Distinct().ToList();
        tables.ShouldContain("purchase_requests");
        tables.ShouldAllBe(t => AuditApp.RequestsTables.Contains(t) && t != "audit_entries");
        await cdc.WaitForAsync(m => m.RoutingKey.EndsWith(".purchase_requests", StringComparison.Ordinal), AuditApp.Timeout);
        cdc.Seen.ShouldNotContain(m => m.RoutingKey.Contains("wolverine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retention_purges_entries_older_than_90_days()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
        // A second Audit host with a fake clock that only runs the retention: no listeners, no message store of its own. Batches of 2,
        // so the purge has to loop.
        await using var host = app.CreateHost(
            services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.DisableAllExternalWolverineTransports();
                services.DisableAllWolverineMessagePersistence();
            },
            new Dictionary<string, string?> { ["Audit:PurgeBatchSize"] = "2" });
        // Starting the host starts the retention's timer on the fake clock.
        var options = host.Services.GetRequiredService<IOptions<AuditOptions>>().Value;
        options.RetentionDays.ShouldBe(90, "from appsettings");
        // The purge runs one interval from now, and keeps what is younger than 90 days then.
        var purgeAt = clock.GetUtcNow() + options.PurgeInterval;
        var expired = purgeAt.AddDays(-options.RetentionDays).AddHours(-1);
        var kept = purgeAt.AddDays(-options.RetentionDays).AddHours(1);

        var database = NewDatabase();
        var old = Enumerable.Range(0, 5).Select(i => Change(database, i, expired)).ToList();
        var recent = Change(database, 99, kept);
        var oldMatch = AttributionMatch.Start($"{database}:1:t:old", expired);
        var recentMatch = AttributionMatch.Start($"{database}:1:t:recent", kept);
        var oldEntry = Entry(database, expired);
        var recentEntry = Entry(database, kept);
        var oldAlert = new OutsideAppAlert(old[0].Id, database, "purchase_requests", "pk-0", null, 1, expired, expired, null);
        var recentAlert = new OutsideAppAlert(recent.Id, database, "purchase_requests", "pk-99", null, 1, kept, kept, null);
        await using (var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Store(old.ToArray());
            session.Store(recent);
            session.Store(oldEntry, recentEntry);
            session.Store(oldMatch, recentMatch);
            session.Store(oldAlert, recentAlert);
            await session.SaveChangesAsync(Ct);
        }

        // Nothing happens before the interval has passed on the (fake) clock.
        clock.Advance(options.PurgeInterval - TimeSpan.FromMinutes(1));
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        (await app.QueryAsync<DataChange>(q => q.Where(c => c.Database == database))).Count.ShouldBe(6);

        clock.Advance(TimeSpan.FromMinutes(1));
        await EventuallyAsync(async () => (await app.QueryAsync<DataChange>(q => q.Where(c => c.Database == database))).Count == 1);

        (await app.QueryAsync<DataChange>(q => q.Where(c => c.Database == database))).ShouldHaveSingleItem().Id.ShouldBe(recent.Id);
        (await app.QueryAsync<InterceptorEntry>(q => q.Where(e => e.Database == database))).ShouldHaveSingleItem().Id.ShouldBe(recentEntry.Id);
        (await app.QueryAsync<AttributionMatch>(q => q.Where(m => m.Id.StartsWith(database)))).ShouldHaveSingleItem().Id.ShouldBe(recentMatch.Id);
        (await app.QueryAsync<OutsideAppAlert>(q => q.Where(a => a.Database == database))).ShouldHaveSingleItem().Id.ShouldBe(recentAlert.Id);
    }

    [Fact]
    public async Task Data_changes_for_request_span_requests_and_budgets()
    {
        await app.EnsureCdcAsync();
        var tenant = AuditApp.NewTenant();
        var (id, _) = await CreateRequestAsync(tenant, "Docking stations");
        await ReserveBudgetAsync(tenant, id, 30_000m);
        await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.SubjectId == id),
            found => found.Any(c => c.Database == AuditApp.BudgetsDb && c.Table == "budget_reservations")
                && found.Any(c => c.Database == AuditApp.RequestsDb && c.Table == "purchase_requests")
                && found.All(c => c.Attribution.Kind == AttributionKind.App));

        using var response = await app.Client.SendAsync(
            AuditApp.Get(DataChangesOf(id), TestAuth.CreateToken(Tom, tenant, "requester")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var page = (await response.Content.ReadFromJsonAsync<PagedResult<DataChangeDto>>(Json, Ct)).ShouldNotBeNull();
        page.Items.Select(c => c.Database).Distinct().ShouldBe([AuditApp.RequestsDb, AuditApp.BudgetsDb], ignoreOrder: true);
        page.Items.Select(c => c.CommittedAt).ShouldBeInOrder(SortDirection.Ascending);

        var created = page.Items.Single(c => c.Table == "purchase_requests" && c.Operation == "Insert");
        created.Attribution.ShouldBe(AttributionKind.App);
        created.UserId.ShouldBe(Tom);
        created.ActorId.ShouldBe("claude-admin");
        created.Fields.ShouldContain(f => f.Column == "title" && f.Old == null && f.New == "Docking stations");

        var reserved = page.Items.Single(c => c.Table == "budget_reservations");
        reserved.UserId.ShouldBe(Tom);
        reserved.ActorId.ShouldBe("service:budgets-api");
        reserved.Fields.ShouldContain(f => f.Column == "amount" && f.New == "30000.00");

        using var otherTenant = await app.Client.SendAsync(
            AuditApp.Get(DataChangesOf(id), TestAuth.CreateToken(Tom, AuditApp.NewTenant(), "requester")), Ct);
        otherTenant.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another tenant's request looks like no request");
    }

    [Fact]
    public async Task Data_change_log_requires_admin_system_403()
    {
        var database = NewDatabase();
        var tenantA = AuditApp.NewTenant();
        var tenantB = AuditApp.NewTenant();
        var outsideA = SyntheticTransaction.New(database, tenantA);
        var outsideB = SyntheticTransaction.New(database, tenantB);
        var tenantless = SyntheticTransaction.New(database, tenantA) with { Table = "request_lines_like", HasTenantColumn = false };
        var appA = SyntheticTransaction.New(database, tenantA);
        foreach (var tx in new[] { outsideA, outsideB, tenantless, appA })
        {
            await InvokeAsync(tx.ChangeJson());
        }

        await InvokeAsync(appA.EntryJson());
        await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Database == database && c.Attribution.Kind == AttributionKind.OutsideApp), found => found.Count == 3, AfterGrace);

        using var requester = await app.Client.SendAsync(
            AuditApp.Get(Log($"database={database}"), TestAuth.CreateToken(Tom, tenantA, "requester")), Ct);
        requester.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A tenant's admin sees that tenant's changes only.
        var asAdmin = await LogAsync(AuditApp.Get(Log($"database={database}&outsideOnly=true"), TestAuth.CreateToken(DemoUsers.Admin, tenantA, "admin")));
        asAdmin.Items.Select(c => c.Id).ShouldBe([outsideA.ChangeId]);
        var allOfA = await LogAsync(AuditApp.Get(Log($"database={database}"), TestAuth.CreateToken(DemoUsers.Admin, tenantA, "admin")));
        allOfA.Items.Select(c => c.Id).ShouldBe([outsideA.ChangeId, appA.ChangeId], ignoreOrder: true);
        var byUser = await LogAsync(AuditApp.Get(Log($"database={database}&userId={Tom}"), TestAuth.CreateToken(DemoUsers.Admin, tenantA, "admin")));
        byUser.Items.Select(c => c.Id).ShouldBe([appA.ChangeId]);

        // The admin key without a tenant is the platform view: every tenant and the changes no tenant can be told for.
        var platform = await LogAsync(AuditApp.GetWithApiKey(Log($"database={database}&outsideOnly=true&pageSize=2"), tenant: null));
        platform.Total.ShouldBe(3);
        platform.Items.Count.ShouldBe(2);
        var platformPage2 = await LogAsync(AuditApp.GetWithApiKey(Log($"database={database}&outsideOnly=true&pageSize=2&page=2"), tenant: null));
        platform.Items.Concat(platformPage2.Items).Select(c => c.Id)
            .ShouldBe([outsideA.ChangeId, outsideB.ChangeId, tenantless.ChangeId], ignoreOrder: true);
        var byTable = await LogAsync(AuditApp.GetWithApiKey(Log($"database={database}&table=request_lines_like"), tenant: null));
        byTable.Items.ShouldHaveSingleItem().TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task Cdc_queue_is_bound_to_the_cdc_exchange_only()
    {
        var cdcSources = await BindingSourcesAsync(Infrastructure.DependencyInjection.CdcQueue);
        var eventSources = await BindingSourcesAsync(Infrastructure.DependencyInjection.EventsQueue);

        cdcSources.ShouldBe([Infrastructure.DependencyInjection.CdcExchange]);
        eventSources.ShouldBe(Infrastructure.DependencyInjection.AuditedExchanges, ignoreOrder: true);
        eventSources.ShouldNotContain(Infrastructure.DependencyInjection.CdcExchange, "Debezium's changes are no business events");
    }

    // ----- helpers -----

    /// <summary>The exchanges a queue is bound to (without the default exchange), from RabbitMQ's management API.</summary>
    private async Task<List<string>> BindingSourcesAsync(string queue)
    {
        var amqp = new Uri(app.Infra.RabbitConnectionString);
        using var http = new HttpClient { BaseAddress = app.Infra.RabbitManagementUri };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(amqp.UserInfo))));
        var bindings = await http.GetFromJsonAsync<JsonElement>($"api/queues/%2F/{queue}/bindings", Ct);
        return [.. bindings.EnumerateArray().Select(b => b.GetProperty("source").GetString()!).Where(s => s.Length > 0).Distinct()];
    }

    private static string NewDatabase() => $"syn{Guid.NewGuid():N}"[..12];

    private static string DataChangesOf(Guid requestId) => $"/api/v1/audit/requests/{requestId}/data-changes";

    private static string Log(string query) => $"/api/v1/audit/data-changes?{query}";

    private async Task<PagedResult<DataChangeDto>> LogAsync(HttpRequestMessage request)
    {
        using var response = await app.Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PagedResult<DataChangeDto>>(Json, Ct)).ShouldNotBeNull();
    }

    private Task PublishAsync(string routingKey, string json) => app.Probe.PublishRawAsync(ServiceInfraFixture.CdcExchange, routingKey, json);

    /// <summary>Hands a Debezium message straight to the Audit service's handler, as its listener would.</summary>
    private Task InvokeAsync(string json) => app.Bus.InvokeAsync(JsonSerializer.Deserialize<DebeziumChange>(json)!);

    private async Task<(Guid Id, string Version)> CreateRequestAsync(string tenant, string title = "Laptops")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Requests)
        {
            Content = JsonContent.Create(new
            {
                title,
                costCenterCode = "CC-100",
                supplierId = "contoso-hardware",
                lines = new[] { new { sku = "LAP-1", quantity = 2 } },
            }),
        };
        AsTomWithTheAdminKey(request, tenant);
        using var response = await app.RequestsClient.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        return (body.GetProperty("id").GetGuid(), body.GetProperty("version").ToString());
    }

    private async Task SubmitAsync(Guid id, string version, string tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Requests}/{id}/submit");
        AsTomWithTheAdminKey(request, tenant);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        using var response = await app.RequestsClient.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
    }

    private static void AsTomWithTheAdminKey(HttpRequestMessage request, string tenant)
    {
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", tenant);
        request.Headers.Add("X-On-Behalf-Of-User", Tom);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
    }

    /// <summary>A reservation written the way the Budgets service writes it: its DbContext with the audit interceptor, as its message handler for tom.</summary>
    private async Task ReserveBudgetAsync(string tenant, Guid requestId, decimal amount)
    {
        var options = new DbContextOptionsBuilder<BudgetsDbContext>()
            .UseNpgsql(app.BudgetsDbConnectionString)
            .AddInterceptors(new AuditSaveChangesInterceptor(TimeProvider.System))
            .Options;
        await using var db = new BudgetsDbContext(options, new Caller(Tom, "service:budgets-api", tenant, "approvals-api", CallerKind.Message));
        var budget = CostCenterBudget.Create(tenant, "CC-100", new FiscalYear(2027), Money.Zar(100_000m));
        budget.Reserve(requestId, Money.Zar(amount));
        db.Budgets.Add(budget);
        await db.SaveChangesAsync(Ct);
    }

    private sealed record Caller(string UserId, string ActorId, string? TenantId, string? CallingService, CallerKind Kind) : ICallerContext;

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Plan 06 carry-over 10 (plan 07 Task 0a): an alert without a tenant (here a request line: the table has no tenant column and the
    /// change no interceptor row) is listed for every tenant's admin, so its text names the table, the operation and the key, never a
    /// value of the row. The same holds for the pgaudit lines, which never have a tenant.
    /// </summary>
    [Fact]
    public async Task Tenantless_alert_text_has_no_row_values()
    {
        await app.EnsureCdcAsync();
        var tenant = AuditApp.NewTenant();
        var (id, _) = await CreateRequestAsync(tenant);
        await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Table == "request_lines" && c.SubjectId == id),
            found => found.Count == 1 && found[0].Attribution.Kind == AttributionKind.App);
        var oldPrice = await ScalarAsync<decimal>(
            app.RequestsDbConnectionString, "SELECT unit_price_excl_vat FROM request_lines WHERE request_id = @id", ("id", id));
        const decimal newPrice = 98765.43m;

        await ExecuteAsync(
            app.RequestsDbConnectionString, "UPDATE request_lines SET unit_price_excl_vat = @price WHERE request_id = @id", ("price", newPrice), ("id", id));

        var key = $"{id},1";
        var outside = (await app.WaitForAsync<DataChange>(
            q => q.Where(c => c.Table == "request_lines" && c.PrimaryKey == key && c.Operation == "Update"),
            found => found.Count == 1 && found[0].Attribution.Kind == AttributionKind.OutsideApp,
            AfterGrace)).Single();
        outside.TenantId.ShouldBeNull("no tenant column and no interceptor row");

        await using var scope = app.Services.CreateAsyncScope();
        var contributors = scope.ServiceProvider.GetServices<ProcureAi.BuildingBlocks.Application.Status.IStatusContributor>().ToList();
        var audit = await contributors.Single(c => c.Name == "audit").CollectAsync(Ct);
        var item = audit.Items.Where(i => i.Text.Contains(key)).ShouldHaveSingleItem();
        item.TenantId.ShouldBeNull();
        item.Text.ShouldBe($"request_lines ({AuditApp.RequestsDb}) changed outside the app, row {key} (update)");

        var pgaudit = await contributors.Single(c => c.Name == "pgaudit").CollectAsync(Ct);
        oldPrice.ShouldNotBe(newPrice);
        string[] values = [Format(oldPrice), Format(newPrice)];
        foreach (var text in audit.Items.Concat(pgaudit.Items).Where(i => i.TenantId is null).Select(i => i.Text))
        {
            foreach (var value in values)
            {
                text.ShouldNotContain(value, Case.Sensitive, "a tenant-less alert never shows a row value");
            }
        }
    }

    private static string Format(decimal value) => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The Debezium messages about one request that the durable inbox has handled (Debezium gives them no message ID).</summary>
    private async Task<long> HandledCdcMessagesAsync()
    {
        await using var connection = new NpgsqlConnection(app.Infra.DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Handled' AND message_type LIKE '%DebeziumChange%'", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<RawMessage> WaitForRawAboutAsync(RabbitProbe probe, string queue, Guid requestId)
    {
        var deadline = DateTime.UtcNow + AuditApp.Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var raw = await probe.WaitForRawAsync(queue, AuditApp.Timeout);
            if (System.Text.Encoding.UTF8.GetString(raw.Body).Contains(requestId.ToString(), StringComparison.Ordinal))
            {
                return raw;
            }
        }

        throw new TimeoutException($"No CDC message about {requestId} arrived on {queue}.");
    }

    private static DataChange Change(string database, int n, DateTimeOffset committedAt) =>
        new($"{database}:{n}:purchase_requests:pk-{n}", database, "purchase_requests", $"pk-{n}", null, null, "Update", n, committedAt,
            "{}", "{}", Attribution.OutsideApp);

    private static InterceptorEntry Entry(string database, DateTimeOffset occurredAt) =>
        new(Guid.NewGuid(), database, 1, "purchase_requests", "pk", Tom, Tom, null, null, null, "[]", occurredAt);

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + AuditApp.Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not hold in time.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }
}
