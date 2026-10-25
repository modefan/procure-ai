using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Requests.Application.Commands;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Domain;
using ProcureAi.Requests.Infrastructure;
using Shouldly;

namespace ProcureAi.Requests.IntegrationTests;

[Collection(InfraCollection.Name)]
public sealed class AuditTests(InfraFixture infra) : IAsyncLifetime
{
    private static readonly TimeSpan CdcTimeout = TimeSpan.FromSeconds(30);

    private RequestsHost _app = null!;

    public async ValueTask InitializeAsync()
    {
        await infra.EnsureDebeziumAsync();
        _app = await RequestsHost.StartAsync(infra.RequestsDbConnectionString, infra.RabbitConnectionString);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static CreateRequest NewCreate(string title = "Laptops") =>
        new(title, "CC-100", "sup-1", [new LineInput("LAP-1", "Laptop", 3, 25200m)]);

    private async Task<RequestDto> CreateAsync(string tenant, string user = "alice")
    {
        _app.Caller.TenantId = tenant;
        _app.Caller.UserId = user;
        return await _app.Bus.InvokeAsync<RequestDto>(NewCreate());
    }

    private async Task<List<AuditEntry>> AuditOfAsync(Guid requestId, string table = "purchase_requests")
    {
        await using var db = new RequestsDbContext(
            new DbContextOptionsBuilder<RequestsDbContext>().UseNpgsql(infra.RequestsDbConnectionString).Options);
        return await db.Set<AuditEntry>()
            .Where(e => e.Table == table && e.PrimaryKey.StartsWith(requestId.ToString()))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();
    }

    [Fact]
    public async Task Update_writes_audit_entry_with_user_and_old_new_values()
    {
        var created = await CreateAsync("audit-update", "alice");

        await _app.Bus.InvokeAsync<RequestDto>(new UpdateRequest(
            created.Id, created.Version, "Monitors", [new LineInput("LAP-1", "Laptop", 3, 25200m)]));

        var update = (await AuditOfAsync(created.Id)).Where(e => e.Operation == AuditOperation.Update).ShouldHaveSingleItem();
        update.UserId.ShouldBe("alice");
        update.TenantId.ShouldBe("audit-update");
        update.PrimaryKey.ShouldBe(created.Id.ToString());
        update.TransactionId.ShouldBeGreaterThan(0);
        var changes = JsonDocument.Parse(update.Changes).RootElement.EnumerateArray().ToList();
        var title = changes.ShouldHaveSingleItem();
        title.GetProperty("column").GetString().ShouldBe("title");
        title.GetProperty("old").GetString().ShouldBe("Laptops");
        title.GetProperty("new").GetString().ShouldBe("Monitors");

        // The create is audited too, as an insert with every column.
        var insert = (await AuditOfAsync(created.Id)).Where(e => e.Operation == AuditOperation.Insert).ShouldHaveSingleItem();
        JsonDocument.Parse(insert.Changes).RootElement.EnumerateArray().Select(c => c.GetProperty("column").GetString()).ShouldContain("status");
    }

    [Fact]
    public async Task Audit_entry_commits_with_the_change()
    {
        var tenant = "audit-rollback";
        var first = PurchaseRequest.CreateDraft(tenant, RequestNumber.From(1), "alice", "First", "CC-1", "sup-1");
        var duplicate = PurchaseRequest.CreateDraft(tenant, RequestNumber.From(1), "alice", "Same number", "CC-1", "sup-1");

        var options = new DbContextOptionsBuilder<RequestsDbContext>()
            .UseNpgsql(infra.RequestsDbConnectionString)
            .AddInterceptors(new AuditSaveChangesInterceptor(TimeProvider.System))
            .Options;
        var caller = new TestCaller { UserId = "alice", TenantId = tenant };

        // Positive control: a valid save writes its audit row, so the empty result below really comes from the rollback.
        var control = PurchaseRequest.CreateDraft(tenant, RequestNumber.From(2), "alice", "Control", "CC-1", "sup-1");
        await using (var db = new RequestsDbContext(options, caller))
        {
            db.PurchaseRequests.Add(control);
            await db.SaveChangesAsync();
        }

        (await AuditOfAsync(control.Id)).ShouldHaveSingleItem();

        await using (var db = new RequestsDbContext(options, caller))
        {
            db.PurchaseRequests.AddRange(first, duplicate);
            // The second row breaks the unique (tenant, number) index, so the whole save rolls back.
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        (await AuditOfAsync(first.Id)).ShouldBeEmpty();
        (await AuditOfAsync(duplicate.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Sensitive_values_are_masked_and_excluded_entities_are_not_audited()
    {
        var connectionString = await infra.CreateEmptyDatabaseAsync("audit_probe");
        var options = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(new AuditSaveChangesInterceptor(TimeProvider.System))
            .Options;
        var id = Guid.NewGuid();
        await using (var db = new ProbeDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Probes.Add(new Probe { Id = id, Name = "visible", Secret = "hunter2" });
            db.Noise.Add(new Noise { Id = Guid.NewGuid(), Text = "ignored" });
            await db.SaveChangesAsync();
            var probe = await db.Probes.SingleAsync();
            probe.Secret = "swordfish";
            await db.SaveChangesAsync();
        }

        await using var read = new ProbeDbContext(options);
        var entries = await read.Set<AuditEntry>().OrderBy(e => e.OccurredAt).ToListAsync();
        entries.Select(e => e.Table).Distinct().ShouldBe(["probes"]);
        entries.Count.ShouldBe(2);
        foreach (var entry in entries)
        {
            entry.Changes.ShouldNotContain("hunter2");
            entry.Changes.ShouldNotContain("swordfish");
        }

        var secret = JsonDocument.Parse(entries[1].Changes).RootElement.EnumerateArray().Single();
        secret.GetProperty("column").GetString().ShouldBe("secret");
        secret.GetProperty("old").GetString().ShouldBe("***");
        secret.GetProperty("new").GetString().ShouldBe("***");
    }

    [Fact]
    public async Task Debezium_publishes_change_with_same_transaction_id()
    {
        await using var cdc = await CdcListener.StartAsync(infra.RabbitConnectionString);
        var created = await CreateAsync("cdc-update");

        await _app.Bus.InvokeAsync<RequestDto>(new UpdateRequest(
            created.Id, created.Version, "Chairs", [new LineInput("LAP-1", "Laptop", 3, 25200m)]));

        var message = await cdc.WaitForAsync(
            m => m.RoutingKey == "requestsdb.public.purchase_requests"
                 && m.Operation == "u"
                 && m.After.GetProperty("id").GetString() == created.Id.ToString(),
            CdcTimeout);
        var update = (await AuditOfAsync(created.Id)).Where(e => e.Operation == AuditOperation.Update).ShouldHaveSingleItem();
        message.TransactionId.ShouldBe(update.TransactionId);
        message.After.GetProperty("title").GetString().ShouldBe("Chairs");
        message.Envelope.GetProperty("source").GetProperty("lsn").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Direct_sql_update_produces_cdc_event_without_audit_entry()
    {
        var created = await CreateAsync("cdc-direct");
        var auditBefore = (await AuditOfAsync(created.Id)).Count;
        await using var cdc = await CdcListener.StartAsync(infra.RabbitConnectionString);

        await using (var connection = new NpgsqlConnection(infra.RequestsDbConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("UPDATE purchase_requests SET title = 'Edited by hand' WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", created.Id);
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        var message = await cdc.WaitForAsync(
            m => m.RoutingKey == "requestsdb.public.purchase_requests"
                 && m.Operation == "u"
                 && m.After.GetProperty("id").GetString() == created.Id.ToString(),
            CdcTimeout);
        message.After.GetProperty("title").GetString().ShouldBe("Edited by hand");
        // Replica identity FULL: the old row is in the event as well.
        message.Before.GetProperty("title").GetString().ShouldBe("Laptops");
        (await AuditOfAsync(created.Id)).Count.ShouldBe(auditBefore);
        (await AuditOfAsync(created.Id)).ShouldNotContain(e => e.TransactionId == message.TransactionId);
    }

    [Fact]
    public async Task Wolverine_tables_are_not_captured()
    {
        var created = await CreateAsync("cdc-wolverine");
        await using var cdc = await CdcListener.StartAsync(infra.RabbitConnectionString);

        // Submitting writes the request and a Wolverine outbox row in one transaction.
        await _app.Bus.InvokeAsync(new SubmitRequest(created.Id, created.Version));
        await cdc.WaitForAsync(
            m => m.RoutingKey == "requestsdb.public.purchase_requests"
                 && m.After.GetProperty("id").GetString() == created.Id.ToString(),
            CdcTimeout);
        // Deterministic part: only the two business tables are in the publication (wolverine.* and audit_entries are not).
        await using (var connection = new NpgsqlConnection(infra.RequestsDbConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT schemaname || '.' || tablename FROM pg_publication_tables WHERE pubname = 'dbz_publication' ORDER BY 1", connection);
            var tables = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            tables.ShouldBe(["public.purchase_requests", "public.request_lines"]);
        }

        // And what Debezium actually sent for that transaction carries nothing from the wolverine schema.
        cdc.Seen.ShouldNotBeEmpty();
        cdc.Seen.ShouldAllBe(m => !m.RoutingKey.Contains("wolverine", StringComparison.OrdinalIgnoreCase));
        cdc.Seen.ShouldNotContain(m => m.RoutingKey.EndsWith("audit_entries", StringComparison.Ordinal));
        cdc.Seen.Select(m => m.RoutingKey).Distinct().ShouldAllBe(
            key => key == "requestsdb.public.purchase_requests" || key == "requestsdb.public.request_lines");
    }

    [Fact]
    public async Task Audit_rows_carry_the_user_and_tenant_of_their_own_scope()
    {
        // Two scopes alive at the same time with different callers (two concurrent requests), saved in crossed order.
        var one = _app.NewScope("user-one", "scope-tenant-1");
        var two = _app.NewScope("user-two", "scope-tenant-2");
        using var scopeOne = one.Scope;
        using var scopeTwo = two.Scope;
        var requestOne = PurchaseRequest.CreateDraft("scope-tenant-1", RequestNumber.From(1), "user-one", "One", "CC-1", "sup-1");
        var requestTwo = PurchaseRequest.CreateDraft("scope-tenant-2", RequestNumber.From(1), "user-two", "Two", "CC-1", "sup-1");
        one.Db.PurchaseRequests.Add(requestOne);
        two.Db.PurchaseRequests.Add(requestTwo);

        await two.Db.SaveChangesAsync();
        await one.Db.SaveChangesAsync();

        var auditOne = (await AuditOfAsync(requestOne.Id)).ShouldHaveSingleItem();
        var auditTwo = (await AuditOfAsync(requestTwo.Id)).ShouldHaveSingleItem();
        (auditOne.UserId, auditOne.TenantId).ShouldBe(("user-one", "scope-tenant-1"));
        (auditTwo.UserId, auditTwo.TenantId).ShouldBe(("user-two", "scope-tenant-2"));
    }
}

public sealed class ProbeAudit : IAuditContext
{
    public string UserId => "bob";

    public string? TenantId => "t1";
}

public sealed class Probe
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    [Sensitive]
    public string Secret { get; set; } = "";
}

[AuditExclude]
public sealed class Noise
{
    public Guid Id { get; set; }

    public string Text { get; set; } = "";
}

public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options), IAuditContextSource
{
    public IAuditContext? AuditContext { get; } = new ProbeAudit();

    public DbSet<Probe> Probes => Set<Probe>();

    public DbSet<Noise> Noise => Set<Noise>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Probe>(e =>
        {
            e.ToTable("probes");
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(p => p.Name).HasColumnName("name");
            e.Property(p => p.Secret).HasColumnName("secret");
        });
        modelBuilder.Entity<Noise>(e =>
        {
            e.ToTable("noise");
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(p => p.Text).HasColumnName("text");
        });
        modelBuilder.AddAuditEntries();
    }
}
