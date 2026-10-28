using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Persistence;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Persistence;

/// <summary>
/// Final review I2: the data-change audit records the service's own data, never Wolverine's message tables (its envelope rows,
/// which carry message bodies) or other internal tables marked <see cref="AuditExcludeAttribute"/>.
/// </summary>
public sealed class AuditInterceptorExclusionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Thing
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";
    }

    /// <summary>Shaped like Wolverine's EF Core envelope entities: a <c>wolverine_*</c> table in the <c>wolverine</c> schema.</summary>
    public sealed class EnvelopeRow
    {
        public Guid Id { get; set; }

        public byte[] Body { get; set; } = [];
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddAuditEntries();
            modelBuilder.AddProcessedWebhooks();
            modelBuilder.Entity<Thing>(e =>
            {
                e.ToTable("things");
                e.Property(t => t.Id).ValueGeneratedNever();
            });
            modelBuilder.Entity<EnvelopeRow>(e =>
            {
                e.ToTable("wolverine_outgoing_envelopes", "wolverine");
                e.Property(t => t.Id).ValueGeneratedNever();
            });
        }
    }

    [Fact]
    public async Task Wolverine_envelope_rows_and_excluded_tables_get_no_audit_entry_but_the_data_does()
    {
        var connectionString = await postgres.CreateDatabaseAsync("audit_exclusions");
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(new AuditSaveChangesInterceptor(TimeProvider.System))
            .Options;
        await using (var setup = new TestDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        await using (var db = new TestDbContext(options))
        {
            db.Add(new Thing { Id = Guid.NewGuid(), Name = "laptop" });
            db.Add(new EnvelopeRow { Id = Guid.NewGuid(), Body = [1, 2, 3] });
            await new WebhookInbox<TestDbContext>(db).TryRecordAsync("supplier", "e1", Ct);
            await db.SaveChangesAsync(Ct);
        }

        await using var read = new TestDbContext(options);
        var tables = await read.Set<AuditEntry>().Select(e => e.Table).ToListAsync(Ct);
        tables.ShouldBe(["things"]);
    }
}
