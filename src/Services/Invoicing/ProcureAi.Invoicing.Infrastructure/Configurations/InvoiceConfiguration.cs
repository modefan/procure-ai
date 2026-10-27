using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Infrastructure.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    /// <summary>The shadow foreign key of invoice lines and payments to their invoice (<c>invoice_id</c>).</summary>
    public const string InvoiceForeignKey = "InvoiceId";

    /// <summary>The shadow foreign key of payment lines to their payment (<c>payment_id</c>).</summary>
    public const string PaymentForeignKey = "PaymentId";

    /// <summary>The shadow concurrency token of a payment row (its <c>xmin</c>): two exports of one payment never both record it.</summary>
    public const string PaymentVersion = "Version";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The match is stored as one jsonb value (a string to EF, so the audit interceptor sees it as one column, as stored).
    private static readonly ValueConverter<MatchResult?, string?> MatchConverter = new(
        match => match == null ? null : JsonSerializer.Serialize(match, Json),
        text => text == null ? null : JsonSerializer.Deserialize<MatchResult>(text, Json));

    private static readonly ValueComparer<MatchResult?> MatchComparer = new(
        (left, right) => JsonSerializer.Serialize(left, Json) == JsonSerializer.Serialize(right, Json),
        match => JsonSerializer.Serialize(match, Json).GetHashCode(StringComparison.Ordinal),
        match => match == null ? null : JsonSerializer.Deserialize<MatchResult>(JsonSerializer.Serialize(match, Json), Json));

    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
        // Empty while a webhook invoice waits for an order Invoicing does not know yet (Invoice.UnassignedTenant).
        builder.Property(i => i.TenantId).HasColumnName("tenant_id").HasMaxLength(InvoicingColumns.TenantLength).IsRequired();
        builder.Property(i => i.OrderId).HasColumnName("order_id");
        builder.Property(i => i.RequestId).HasColumnName("request_id");
        builder.Property(i => i.SupplierId).HasColumnName("supplier_id").HasMaxLength(InvoicingColumns.SupplierLength).IsRequired();
        builder.Property(i => i.SupplierInvoiceNumber).HasColumnName("supplier_invoice_number")
            .HasMaxLength(InvoicingColumns.InvoiceNumberLength).IsRequired();
        builder.Property(i => i.InvoiceDate).HasColumnName("invoice_date");
        builder.Property(i => i.Sequence).HasColumnName("sequence");
        builder.Property(i => i.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.StatedTotalInclVat).HasColumnName("stated_total_incl_vat").HasColumnType(InvoicingColumns.Money)
            .HasConversion(money => money.Amount, amount => Money.Zar(amount));
        // The raw webhook body or entry, queryable in Postgres (jsonb normalises whitespace and key order).
        builder.Property(i => i.Document).HasColumnName("document").HasColumnType("jsonb").IsRequired();
        builder.Property(i => i.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.OnHold).HasColumnName("on_hold");
        builder.Property(i => i.HoldReason).HasColumnName("hold_reason").HasMaxLength(InvoicingColumns.ReasonLength);
        builder.Property(i => i.Match).HasColumnName("match").HasColumnType("jsonb")
            .HasConversion(MatchConverter, MatchComparer);
        builder.Property(i => i.ReceivedAt).HasColumnName("received_at");
        builder.Property(i => i.Revision).HasColumnName("revision");

        // xmin: every state change of the invoice (attach, match, hold, release, payment) changes Revision and so writes this row.
        builder.Property(i => i.Version).HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        // One invoice per supplier invoice number in a tenant: two deliveries or entries racing collide here and the loser, retried,
        // finds the winner. A waiting invoice has no tenant yet, so it is unique per order instead.
        builder.HasIndex(i => new { i.TenantId, i.SupplierId, i.SupplierInvoiceNumber }).IsUnique()
            .HasFilter("tenant_id <> ''").HasDatabaseName("ix_invoices_tenant_supplier_number");
        builder.HasIndex(i => new { i.OrderId, i.SupplierId, i.SupplierInvoiceNumber }).IsUnique()
            .HasFilter("tenant_id = ''").HasDatabaseName("ix_invoices_waiting_order_supplier_number");
        builder.HasIndex(i => new { i.TenantId, i.OrderId });
        builder.HasIndex(i => new { i.TenantId, i.ReceivedAt });

        builder.Ignore(i => i.TotalExclVat);
        builder.Ignore(i => i.Vat);
        builder.Ignore(i => i.TotalInclVat);
        builder.Ignore(i => i.HasTenant);
        builder.Ignore(i => i.DomainEvents);

        builder.OwnsMany(i => i.Lines, lines =>
        {
            lines.ToTable("invoice_lines");
            lines.WithOwner().HasForeignKey(InvoiceForeignKey);
            lines.Property<Guid>(InvoiceForeignKey).HasColumnName("invoice_id");
            lines.HasKey(InvoiceForeignKey, nameof(InvoiceLine.LineNo));
            lines.Property(l => l.LineNo).HasColumnName("line_no").ValueGeneratedNever();
            lines.Property(l => l.Sku).HasColumnName("sku").HasMaxLength(InvoicingColumns.SkuLength).IsRequired();
            lines.Property(l => l.Description).HasColumnName("description").HasMaxLength(InvoicingColumns.DescriptionLength).IsRequired();
            lines.Property(l => l.Quantity).HasColumnName("quantity");
            lines.Property(l => l.UnitPriceExclVat).HasColumnName("unit_price_excl_vat").HasColumnType(InvoicingColumns.Money)
                .HasConversion(money => money.Amount, amount => Money.Zar(amount));
            lines.Property(l => l.Paid).HasColumnName("paid");
            lines.Ignore(l => l.LineTotalExclVat);
        });
        builder.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(i => i.Payments, payments =>
        {
            payments.ToTable("payments");
            payments.WithOwner().HasForeignKey(InvoiceForeignKey);
            payments.Property<Guid>(InvoiceForeignKey).HasColumnName("invoice_id");
            payments.HasKey(p => p.Id);
            payments.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            payments.Property(p => p.Number).HasColumnName("number")
                .HasConversion(number => number.Value, value => PaymentNumber.FromValue(value));
            payments.Property(p => p.TotalExclVat).HasColumnName("total_excl_vat").HasColumnType(InvoicingColumns.Money)
                .HasConversion(money => money.Amount, amount => Money.Zar(amount));
            payments.Property(p => p.Vat).HasColumnName("vat").HasColumnType(InvoicingColumns.Money)
                .HasConversion(money => money.Amount, amount => Money.Zar(amount));
            payments.Property(p => p.TotalInclVat).HasColumnName("total_incl_vat").HasColumnType(InvoicingColumns.Money)
                .HasConversion(money => money.Amount, amount => Money.Zar(amount));
            payments.Property(p => p.ReleasedBy).HasColumnName("released_by").HasMaxLength(InvoicingColumns.UserLength).IsRequired();
            payments.Property(p => p.ReleasedAt).HasColumnName("released_at");
            payments.Property(p => p.ExportStatus).HasColumnName("export_status").HasConversion<string>().HasMaxLength(20);
            payments.Property(p => p.ErpReference).HasColumnName("erp_reference").HasMaxLength(InvoicingColumns.ErpReferenceLength);
            payments.Property(p => p.ExportAttempts).HasColumnName("export_attempts");
            payments.Property(p => p.LastExportError).HasColumnName("last_export_error").HasMaxLength(InvoicingColumns.ReasonLength);
            payments.Property<uint>(PaymentVersion).HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            payments.HasIndex(InvoiceForeignKey);

            payments.OwnsMany(p => p.Lines, lines =>
            {
                lines.ToTable("payment_lines");
                lines.WithOwner().HasForeignKey(PaymentForeignKey);
                lines.Property<Guid>(PaymentForeignKey).HasColumnName("payment_id");
                lines.HasKey(PaymentForeignKey, nameof(PaymentLine.LineNo));
                lines.Property(l => l.LineNo).HasColumnName("line_no").ValueGeneratedNever();
                lines.Property(l => l.Sku).HasColumnName("sku").HasMaxLength(InvoicingColumns.SkuLength).IsRequired();
                lines.Property(l => l.Quantity).HasColumnName("quantity");
                lines.Property(l => l.AmountExclVat).HasColumnName("amount_excl_vat").HasColumnType(InvoicingColumns.Money)
                    .HasConversion(money => money.Amount, amount => Money.Zar(amount));
            });
            payments.Navigation(p => p.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        builder.Navigation(i => i.Payments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
