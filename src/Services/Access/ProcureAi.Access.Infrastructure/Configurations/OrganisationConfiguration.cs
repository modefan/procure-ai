using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Access.Infrastructure.Configurations;

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("departments");
        builder.HasKey(d => new { d.TenantId, d.Code });
        builder.Property(d => d.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
        builder.Property(d => d.Code).HasColumnName("code").HasMaxLength(50);
        builder.Property(d => d.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(d => d.HeadUserId).HasColumnName("head_user_id").HasMaxLength(100);
    }
}

internal sealed class CostCenterConfiguration : IEntityTypeConfiguration<CostCenter>
{
    public void Configure(EntityTypeBuilder<CostCenter> builder)
    {
        builder.ToTable("cost_centers");
        builder.HasKey(c => new { c.TenantId, c.Code });
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
        builder.Property(c => c.Code).HasColumnName("code").HasMaxLength(50);
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(c => c.DepartmentCode).HasColumnName("department_code").HasMaxLength(50).IsRequired();
        builder.Property(c => c.ManagerUserId).HasColumnName("manager_user_id").HasMaxLength(100);
    }
}

internal sealed class TenantUserConfiguration : IEntityTypeConfiguration<TenantUser>
{
    public void Configure(EntityTypeBuilder<TenantUser> builder)
    {
        builder.ToTable("tenant_users");
        builder.HasKey(u => new { u.TenantId, u.UserId });
        builder.Property(u => u.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
        builder.Property(u => u.UserId).HasColumnName("user_id").HasMaxLength(100);
        builder.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(200).IsRequired();
        builder.Property(u => u.Roles).HasColumnName("roles").HasField("_roles").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Property(u => u.CostCenterCodes).HasColumnName("cost_center_codes")
            .HasField("_costCenterCodes").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Property(u => u.ApprovalLimit).HasColumnName("approval_limit").HasColumnType("numeric(18,2)")
            .HasConversion(money => money.Amount, amount => Money.Zar(amount));
        builder.Property(u => u.IsFinanceApprover).HasColumnName("is_finance_approver");

        // The Postgres system column xmin changes on every write of the row: an optimistic concurrency token.
        builder.Property(u => u.Version).HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}
