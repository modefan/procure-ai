using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcureAi.BuildingBlocks.Application;
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

    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

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
        _app.Caller.ActorId = user;
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
        update.ActorId.ShouldBe("alice");
        update.CallingService.ShouldBeNull();
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

    /// <summary>
    /// An update that changes only the lines (or nothing) still sends an UPDATE of the request row, so its version moves. The
    /// interceptor must write a row for every UPDATE it lets through, or Audit sees a CDC change of purchase_requests without
    /// an app row in its transaction and flags it "changed outside the app" (a false alert on an ordinary edit).
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    public async Task Update_without_a_changed_request_column_still_audits_the_request_row(int quantity)
    {
        var created = await CreateAsync("audit-touch", "alice");

        // Same title; quantity 3 is the very same line again (nothing changed at all), 5 changes only the line.
        var updated = await _app.Bus.InvokeAsync<RequestDto>(new UpdateRequest(
            created.Id, created.Version, "Laptops", [new LineInput("LAP-1", "Laptop", quantity, 25200m)]));

        updated.Version.ShouldNotBe(created.Version, "the request row was written");
        var update = (await AuditOfAsync(created.Id)).Where(e => e.Operation == AuditOperation.Update).ShouldHaveSingleItem();
        update.UserId.ShouldBe("alice");
        // Same transaction as the line changes of this save (the top-level xid, which is what Debezium reports as txId).
        var lineDelete = (await AuditOfAsync(created.Id, "request_lines")).Where(e => e.Operation == AuditOperation.Delete).ShouldHaveSingleItem();
        update.TransactionId.ShouldBe(lineDelete.TransactionId);
        JsonDocument.Parse(update.Changes).RootElement.EnumerateArray().ShouldBeEmpty("no request column changed value");
    }

    [Fact]
    public async Task Api_key_on_behalf_of_user_audits_user_and_actor()
    {
        await using var factory = new ApiFactory(infra);
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/requests")
        {
            Content = JsonContent.Create(new
            {
                title = "On behalf",
                costCenterCode = "CC-100",
                supplierId = "sup-1",
                lines = new[] { new { sku = "LAP-1", description = "Laptop", quantity = 3, unitPriceExclVat = 25200m } },
            }),
        };
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", "audit-actor");
        request.Headers.Add("X-On-Behalf-Of-User", "tom");
        request.Headers.Add("X-Calling-Service", "approvals-api");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<RequestDto>(ResponseJson, TestContext.Current.CancellationToken))!;
        var insert = (await AuditOfAsync(created.Id)).ShouldHaveSingleItem();
        insert.UserId.ShouldBe("tom");
        insert.ActorId.ShouldBe("claude-admin");
        insert.CallingService.ShouldBe("approvals-api");
        insert.TenantId.ShouldBe("audit-actor");
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
    public async Task Captures_purchase_requests_and_audit_entries()
    {
        await using var cdc = await CdcListener.StartAsync(infra.RabbitConnectionString);
        var created = await CreateAsync("cdc-both");

        // The two messages arrive in either order (one transaction): wait until both are among the messages seen.
        var id = created.Id.ToString();
        bool IsRequest(CdcMessage m) =>
            m.RoutingKey == "requestsdb.public.purchase_requests" && m.Operation == "c" && m.After.GetProperty("id").GetString() == id;
        bool IsAudit(CdcMessage m) =>
            m.RoutingKey == "requestsdb.public.audit_entries" && m.Operation == "c"
            && m.After.GetProperty("table_name").GetString() == "purchase_requests"
            && m.After.GetProperty("primary_key").GetString() == id;
        await cdc.WaitForAsync(m => cdc.Seen.Any(IsRequest) && cdc.Seen.Any(IsAudit), CdcTimeout);
        var requestMessage = cdc.Seen.First(IsRequest);
        var auditMessage = cdc.Seen.First(IsAudit);

        // The interceptor row and the business change share the transaction, so Audit can match them by transaction ID.
        auditMessage.TransactionId.ShouldBe(requestMessage.TransactionId);
        auditMessage.After.GetProperty("table_name").GetString().ShouldBe("purchase_requests");
        auditMessage.After.GetProperty("tenant_id").GetString().ShouldBe("cdc-both");
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
        // Deterministic part: only the business tables and audit_entries are in the publication (wolverine.* is not).
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

            tables.ShouldBe(["public.audit_entries", "public.purchase_requests", "public.request_lines"]);
        }

        // And what Debezium actually sent for that transaction carries nothing from the wolverine schema.
        cdc.Seen.ShouldNotBeEmpty();
        cdc.Seen.ShouldAllBe(m => !m.RoutingKey.Contains("wolverine", StringComparison.OrdinalIgnoreCase));
        cdc.Seen.Select(m => m.RoutingKey).Distinct().ShouldAllBe(
            key => key == "requestsdb.public.purchase_requests"
                   || key == "requestsdb.public.request_lines"
                   || key == "requestsdb.public.audit_entries");
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

public sealed class ProbeCaller : ICallerContext
{
    public string UserId => "bob";

    public string ActorId => "bob";

    public string? TenantId => "t1";

    public string? CallingService => null;

    public CallerKind Kind => CallerKind.User;
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
    public ICallerContext? Caller { get; } = new ProbeCaller();

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
