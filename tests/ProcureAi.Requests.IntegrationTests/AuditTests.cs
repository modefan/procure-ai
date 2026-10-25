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
            .AddInterceptors(new AuditSaveChangesInterceptor(new TestAuditContext("alice", tenant), TimeProvider.System))
            .Options;
        await using (var db = new RequestsDbContext(options))
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
            .AddInterceptors(new AuditSaveChangesInterceptor(new TestAuditContext("bob", "t1"), TimeProvider.System))
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
        // The wolverine rows are deleted again once the message is sent; give those changes time to show up.
        await Task.Delay(TimeSpan.FromSeconds(5));
        try
        {
            await cdc.WaitForAsync(_ => false, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Expected: just draining what is left.
        }

        cdc.Seen.ShouldNotBeEmpty();
        cdc.Seen.ShouldAllBe(m => !m.RoutingKey.Contains("wolverine", StringComparison.OrdinalIgnoreCase));
        cdc.Seen.ShouldNotContain(m => m.RoutingKey.EndsWith("audit_entries", StringComparison.Ordinal));
        cdc.Seen.Select(m => m.RoutingKey).Distinct().ShouldAllBe(
            key => key == "requestsdb.public.purchase_requests" || key == "requestsdb.public.request_lines");
    }

    private sealed class TestAuditContext(string userId, string? tenantId) : IAuditContext
    {
        public string UserId { get; } = userId;

        public string? TenantId { get; } = tenantId;
    }
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

public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options)
{
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
