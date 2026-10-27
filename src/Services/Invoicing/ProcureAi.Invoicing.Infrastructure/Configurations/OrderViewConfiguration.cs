using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Infrastructure.Configurations;

internal sealed class OrderViewConfiguration : IEntityTypeConfiguration<OrderView>
{
    /// <summary>The shadow foreign key of order view lines to their order (<c>order_id</c>).</summary>
    public const string OrderForeignKey = "OrderId";

    public void Configure(EntityTypeBuilder<OrderView> builder)
    {
        builder.ToTable("order_views");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(o => o.TenantId).HasColumnName("tenant_id").HasMaxLength(InvoicingColumns.TenantLength).IsRequired();
        builder.Property(o => o.RequestId).HasColumnName("request_id");
        builder.Property(o => o.OrderNumber).HasColumnName("order_number").HasMaxLength(50).IsRequired();
        builder.Property(o => o.SupplierId).HasColumnName("supplier_id").HasMaxLength(InvoicingColumns.SupplierLength).IsRequired();
        builder.Property(o => o.CostCenterCode).HasColumnName("cost_center_code").HasMaxLength(50).IsRequired();
        builder.Property(o => o.RequesterId).HasColumnName("requester_id").HasMaxLength(100).IsRequired();
        builder.Property(o => o.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.PaidInclVat).HasColumnName("paid_incl_vat").HasColumnType(InvoicingColumns.Money)
            .HasConversion(money => money.Amount, amount => Money.Zar(amount));
        builder.Property(o => o.SettledAt).HasColumnName("settled_at");
        builder.Property(o => o.Revision).HasColumnName("revision");

        // xmin changes on every write of the row: the optimistic concurrency token of the whole order view, because every match,
        // receipt, close short and payment changes Revision and so writes the row. Payments of one order are serialised by it.
        builder.Property(o => o.Version).HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasIndex(o => o.TenantId);
        builder.Ignore(o => o.IsSettled);
        builder.Ignore(o => o.DomainEvents);

        builder.OwnsMany(o => o.Lines, lines =>
        {
            lines.ToTable("order_view_lines");
            lines.WithOwner().HasForeignKey(OrderForeignKey);
            lines.Property<Guid>(OrderForeignKey).HasColumnName("order_id");
            lines.HasKey(OrderForeignKey, nameof(OrderViewLine.LineNo));
            lines.Property(l => l.LineNo).HasColumnName("line_no").ValueGeneratedNever();
            lines.Property(l => l.Sku).HasColumnName("sku").HasMaxLength(InvoicingColumns.SkuLength).IsRequired();
            lines.Property(l => l.Description).HasColumnName("description").HasMaxLength(InvoicingColumns.DescriptionLength).IsRequired();
            lines.Property(l => l.Ordered).HasColumnName("ordered");
            lines.Property(l => l.UnitPriceExclVat).HasColumnName("unit_price_excl_vat").HasColumnType(InvoicingColumns.Money)
                .HasConversion(money => money.Amount, amount => Money.Zar(amount));
            lines.Property(l => l.Received).HasColumnName("received");
            lines.Property(l => l.ClosedShort).HasColumnName("closed_short");
            lines.Property(l => l.Paid).HasColumnName("paid");
        });
        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>Column sizes of Invoicing's tables, also the limits the Api checks before anything reaches the database.</summary>
public static class InvoicingColumns
{
    public const string Money = "numeric(18,2)";
    public const int TenantLength = 100;
    public const int SupplierLength = 100;
    public const int InvoiceNumberLength = 64;
    public const int SkuLength = 100;
    public const int DescriptionLength = 500;
    public const int ReasonLength = 500;
    public const int UserLength = 100;
    public const int ErpReferenceLength = 100;
}
