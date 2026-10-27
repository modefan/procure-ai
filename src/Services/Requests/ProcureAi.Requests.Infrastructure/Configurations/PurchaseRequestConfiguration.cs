using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Infrastructure.Configurations;

internal sealed class PurchaseRequestConfiguration : IEntityTypeConfiguration<PurchaseRequest>
{
    private const string LinesForeignKey = "RequestId";

    public void Configure(EntityTypeBuilder<PurchaseRequest> builder)
    {
        builder.ToTable("purchase_requests");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasMaxLength(100).IsRequired();
        builder.Property(r => r.Number).HasColumnName("number")
            .HasConversion(number => number.Sequence, sequence => RequestNumber.From(sequence));
        builder.Property(r => r.RequesterId).HasColumnName("requester_id").HasMaxLength(100).IsRequired();
        builder.Property(r => r.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(r => r.CostCenterCode).HasColumnName("cost_center_code").HasMaxLength(50).IsRequired();
        builder.Property(r => r.SupplierId).HasColumnName("supplier_id").HasMaxLength(100).IsRequired();
        builder.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.SubmittedAt).HasColumnName("submitted_at");
        builder.Property(r => r.ApprovedAt).HasColumnName("approved_at");
        builder.Property(r => r.RejectedAt).HasColumnName("rejected_at");
        builder.Property(r => r.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(500);
        builder.Property(r => r.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(r => r.CancelledBy).HasColumnName("cancelled_by").HasMaxLength(100);
        builder.Property(r => r.CancellationReason).HasColumnName("cancellation_reason").HasMaxLength(500);

        // The Postgres system column xmin changes on every write of the row: an optimistic concurrency token.
        builder.Property(r => r.Version).HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasIndex(r => new { r.TenantId, r.Number }).IsUnique();
        builder.Ignore(r => r.TotalExclVat);
        builder.Ignore(r => r.Vat);
        builder.Ignore(r => r.TotalInclVat);
        builder.Ignore(r => r.DomainEvents);

        builder.OwnsMany(r => r.Lines, lines =>
        {
            lines.ToTable("request_lines");
            lines.WithOwner().HasForeignKey(LinesForeignKey);
            lines.Property<Guid>(LinesForeignKey).HasColumnName("request_id");
            lines.HasKey(LinesForeignKey, nameof(RequestLine.LineNo));
            lines.Property(l => l.LineNo).HasColumnName("line_no").ValueGeneratedNever();
            lines.Property(l => l.Sku).HasColumnName("sku").HasMaxLength(100).IsRequired();
            lines.Property(l => l.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
            lines.Property(l => l.Quantity).HasColumnName("quantity");
            lines.Property(l => l.UnitPriceExclVat).HasColumnName("unit_price_excl_vat").HasColumnType("numeric(18,2)")
                .HasConversion(money => money.Amount, amount => Money.Zar(amount));
            lines.Ignore(l => l.TotalExclVat);
        });
        builder.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
