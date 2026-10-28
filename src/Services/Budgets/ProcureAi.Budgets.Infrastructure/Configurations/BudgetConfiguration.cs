using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProcureAi.Budgets.Domain;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Budgets.Infrastructure.Configurations;

internal sealed class CostCenterBudgetConfiguration : IEntityTypeConfiguration<CostCenterBudget>
{
    private const string ReservationsForeignKey = "BudgetId";
    private const string MoneyColumn = "numeric(18,2)";

    public void Configure(EntityTypeBuilder<CostCenterBudget> builder)
    {
        builder.ToTable("budgets");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(b => b.TenantId).HasColumnName("tenant_id").HasMaxLength(100).IsRequired();
        builder.Property(b => b.CostCenterCode).HasColumnName("cost_center_code").HasMaxLength(50).IsRequired();
        builder.Property(b => b.FiscalYear).HasColumnName("fiscal_year")
            .HasConversion(year => year.EndYear, endYear => new FiscalYear(endYear));
        builder.Property(b => b.Amount).HasColumnName("amount").HasColumnType(MoneyColumn)
            .HasConversion(money => money.Amount, amount => Money.Zar(amount));
        builder.Property(b => b.Reserved).HasColumnName("reserved").HasColumnType(MoneyColumn)
            .HasConversion(money => money.Amount, amount => Money.Zar(amount));
        builder.Property(b => b.Spent).HasColumnName("spent").HasColumnType(MoneyColumn)
            .HasConversion(money => money.Amount, amount => Money.Zar(amount));
        builder.Ignore(b => b.Available);

        // The Postgres system column xmin changes on every write of the row: an optimistic concurrency token.
        builder.Property(b => b.Version).HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasIndex(b => new { b.TenantId, b.CostCenterCode, b.FiscalYear }).IsUnique();

        builder.OwnsMany(b => b.Reservations, reservations =>
        {
            reservations.ToTable("budget_reservations");
            reservations.WithOwner().HasForeignKey(ReservationsForeignKey);
            reservations.Property<Guid>(ReservationsForeignKey).HasColumnName("budget_id");
            reservations.HasKey(ReservationsForeignKey, nameof(BudgetReservation.RequestId));
            reservations.Property(r => r.RequestId).HasColumnName("request_id").ValueGeneratedNever();
            reservations.Property(r => r.Amount).HasColumnName("amount").HasColumnType(MoneyColumn)
                .HasConversion(money => money.Amount, amount => Money.Zar(amount));
            reservations.Property(r => r.Spent).HasColumnName("spent").HasColumnType(MoneyColumn)
                .HasConversion(money => money.Amount, amount => Money.Zar(amount)).HasDefaultValue(Money.Zero);
            reservations.Property(r => r.IsReleased).HasColumnName("is_released");
            reservations.Ignore(r => r.Remaining);
        });
        builder.Navigation(b => b.Reservations).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(b => b.Spends, spends =>
        {
            spends.ToTable("budget_spends");
            spends.WithOwner().HasForeignKey(nameof(BudgetSpend.BudgetId));
            spends.HasKey(nameof(BudgetSpend.BudgetId), nameof(BudgetSpend.PaymentId));
            spends.Property(s => s.BudgetId).HasColumnName("budget_id");
            spends.Property(s => s.PaymentId).HasColumnName("payment_id").ValueGeneratedNever();
            spends.Property(s => s.RequestId).HasColumnName("request_id");
            spends.Property(s => s.Amount).HasColumnName("amount").HasColumnType(MoneyColumn)
                .HasConversion(money => money.Amount, amount => Money.Zar(amount));
            spends.Property(s => s.RecordedAt).HasColumnName("recorded_at");
        });
        builder.Navigation(b => b.Spends).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class RequestMarkerConfiguration : IEntityTypeConfiguration<RequestMarker>
{
    public void Configure(EntityTypeBuilder<RequestMarker> builder)
    {
        builder.ToTable("request_markers");
        builder.HasKey(m => new { m.TenantId, m.RequestId });
        builder.Property(m => m.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
        builder.Property(m => m.RequestId).HasColumnName("request_id").ValueGeneratedNever();
        builder.Property(m => m.IsReleased).HasColumnName("is_released");
    }
}
