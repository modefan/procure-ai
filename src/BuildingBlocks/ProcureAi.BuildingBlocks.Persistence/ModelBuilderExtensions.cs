using Microsoft.EntityFrameworkCore;

namespace ProcureAi.BuildingBlocks.Persistence;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// SQL default for <see cref="AuditEntry.TransactionId"/>: evaluated by Postgres inside the transaction that inserts
    /// the row. <c>xid8</c> to <c>xid</c> keeps the lower 32 bits, which is the transaction ID in the WAL and in Debezium.
    /// </summary>
    public const string TransactionIdSql = "pg_current_xact_id()::xid::text::bigint";

    /// <summary>Adds the <c>audit_entries</c> table to the model.</summary>
    public static ModelBuilder AddAuditEntries(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEntry>(entry =>
        {
            entry.ToTable("audit_entries");
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entry.Property(e => e.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
            entry.Property(e => e.UserId).HasColumnName("user_id").HasMaxLength(100).IsRequired();
            entry.Property(e => e.ActorId).HasColumnName("actor_id").HasMaxLength(100).IsRequired();
            entry.Property(e => e.CallingService).HasColumnName("calling_service").HasMaxLength(100);
            entry.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            entry.Property(e => e.TransactionId).HasColumnName("transaction_id")
                .HasDefaultValueSql(TransactionIdSql).ValueGeneratedOnAdd();
            entry.Property(e => e.Table).HasColumnName("table_name").HasMaxLength(100).IsRequired();
            entry.Property(e => e.PrimaryKey).HasColumnName("primary_key").HasMaxLength(200).IsRequired();
            entry.Property(e => e.Operation).HasColumnName("operation").HasConversion<string>().HasMaxLength(10);
            entry.Property(e => e.Changes).HasColumnName("changes").HasColumnType("jsonb").IsRequired();
            entry.Property(e => e.OccurredAt).HasColumnName("occurred_at");
            entry.HasIndex(e => e.TransactionId);
            entry.HasIndex(e => new { e.Table, e.PrimaryKey });
        });
        return modelBuilder;
    }
}
