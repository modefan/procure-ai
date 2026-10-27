using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Purchasing.Domain;

namespace ProcureAi.Purchasing.Infrastructure;

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    /// <summary>The shadow foreign key of lines and receipts to their order (<c>order_id</c>).</summary>
    public const string OrderForeignKey = "OrderId";

    /// <summary>The shadow foreign key of receipt lines to their receipt (<c>receipt_id</c>).</summary>
    public const string ReceiptForeignKey = "ReceiptId";

    public const int ReferenceLength = 100;
    public const int ReasonLength = 500;

    private const string MoneyColumn = "numeric(18,2)";

    private static readonly ValueConverter<OrderNumber?, int?> NumberConverter = new(
        number => number.HasValue ? number.Value.Value : null,
        value => value.HasValue ? OrderNumber.FromValue(value.Value) : null);

    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("purchase_orders");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(o => o.TenantId).HasColumnName("tenant_id").HasMaxLength(100).IsRequired();
        builder.Property(o => o.RequestId).HasColumnName("request_id");
        // Null only on a tombstone (a cancel that came before the order).
        builder.Property(o => o.Number).HasColumnName("number").HasConversion(NumberConverter);
        builder.Property(o => o.RequestNumber).HasColumnName("request_number").HasMaxLength(50).IsRequired();
        builder.Property(o => o.RequesterId).HasColumnName("requester_id").HasMaxLength(100).IsRequired();
        builder.Property(o => o.CostCenterCode).HasColumnName("cost_center_code").HasMaxLength(50).IsRequired();
        builder.Property(o => o.SupplierId).HasColumnName("supplier_id").HasMaxLength(100).IsRequired();
        builder.Property(o => o.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.SupplierReference).HasColumnName("supplier_reference").HasMaxLength(ReferenceLength);
        builder.Property(o => o.ExpectedDelivery).HasColumnName("expected_delivery");
        builder.Property(o => o.SupplierAttempts).HasColumnName("supplier_attempts");
        builder.Property(o => o.LastSupplierError).HasColumnName("last_supplier_error").HasMaxLength(ReasonLength);
        builder.Property(o => o.NextSupplierAttemptAt).HasColumnName("next_supplier_attempt_at");
        builder.Property(o => o.CancelReason).HasColumnName("cancel_reason").HasMaxLength(ReasonLength);
        builder.Property(o => o.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(o => o.SupplierCancelPending).HasColumnName("supplier_cancel_pending");
        builder.Property(o => o.CreatedAt).HasColumnName("created_at");

        // The Postgres system column xmin changes on every write of the row: an optimistic concurrency token. PurchasingDbContext
        // writes the order row whenever a line or receipt changes, so it guards the whole aggregate.
        builder.Property(o => o.Version).HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        // One order (or tombstone) per request: two creates, or a create and a cancel, racing for the same request collide here.
        builder.HasIndex(o => new { o.TenantId, o.RequestId }).IsUnique();
        builder.HasIndex(o => new { o.TenantId, o.Number }).IsUnique();
        builder.HasIndex(o => new { o.TenantId, o.CreatedAt });

        builder.Ignore(o => o.TotalExclVat);
        builder.Ignore(o => o.Vat);
        builder.Ignore(o => o.TotalInclVat);
        builder.Ignore(o => o.DomainEvents);

        builder.OwnsMany(o => o.Lines, lines =>
        {
            lines.ToTable("order_lines");
            lines.WithOwner().HasForeignKey(OrderForeignKey);
            lines.Property<Guid>(OrderForeignKey).HasColumnName("order_id");
            lines.HasKey(OrderForeignKey, nameof(OrderLine.LineNo));
            lines.Property(l => l.LineNo).HasColumnName("line_no").ValueGeneratedNever();
            lines.Property(l => l.Sku).HasColumnName("sku").HasMaxLength(100).IsRequired();
            lines.Property(l => l.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
            lines.Property(l => l.Quantity).HasColumnName("quantity");
            lines.Property(l => l.UnitPriceExclVat).HasColumnName("unit_price_excl_vat").HasColumnType(MoneyColumn)
                .HasConversion(money => money.Amount, amount => Money.Zar(amount));
            lines.Property(l => l.Received).HasColumnName("received");
            lines.Property(l => l.ClosedShort).HasColumnName("closed_short");
            lines.Ignore(l => l.Outstanding);
            lines.Ignore(l => l.LineTotalExclVat);
        });
        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(o => o.Receipts, receipts =>
        {
            receipts.ToTable("goods_receipts");
            receipts.WithOwner().HasForeignKey(OrderForeignKey);
            receipts.Property<Guid>(OrderForeignKey).HasColumnName("order_id");
            receipts.HasKey(r => r.Id);
            receipts.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            receipts.Property(r => r.ReceivedBy).HasColumnName("received_by").HasMaxLength(100).IsRequired();
            receipts.Property(r => r.ReceivedAt).HasColumnName("received_at");
            receipts.Property(r => r.DeliveryNote).HasColumnName("delivery_note").HasMaxLength(ReasonLength);
            receipts.HasIndex(OrderForeignKey);

            receipts.OwnsMany(r => r.Lines, lines =>
            {
                lines.ToTable("receipt_lines");
                lines.WithOwner().HasForeignKey(ReceiptForeignKey);
                lines.Property<Guid>(ReceiptForeignKey).HasColumnName("receipt_id");
                lines.HasKey(ReceiptForeignKey, nameof(ReceiptLine.LineNo));
                lines.Property(l => l.LineNo).HasColumnName("line_no").ValueGeneratedNever();
                lines.Property(l => l.Quantity).HasColumnName("quantity");
            });
            receipts.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        builder.Navigation(o => o.Receipts).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
