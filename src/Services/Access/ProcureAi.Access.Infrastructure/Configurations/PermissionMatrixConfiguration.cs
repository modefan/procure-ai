using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProcureAi.Access.Domain;

namespace ProcureAi.Access.Infrastructure.Configurations;

internal sealed class PermissionMatrixConfiguration : IEntityTypeConfiguration<PermissionMatrix>
{
    private const string GrantsForeignKey = "TenantId";

    public void Configure(EntityTypeBuilder<PermissionMatrix> builder)
    {
        builder.ToTable("permission_matrices");
        builder.HasKey(m => m.TenantId);
        builder.Property(m => m.TenantId).HasColumnName("tenant_id").HasMaxLength(100).ValueGeneratedNever();
        builder.Property(m => m.UpdatedAt).HasColumnName("updated_at");

        // The Postgres system column xmin changes on every write of the row: the version of the whole matrix (updated_at changes with every edit, so the row is always written).
        builder.Property(m => m.Version).HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.OwnsMany(m => m.Grants, grants =>
        {
            grants.ToTable("permission_grants");
            grants.WithOwner().HasForeignKey(GrantsForeignKey);
            grants.Property<string>(GrantsForeignKey).HasColumnName("tenant_id").HasMaxLength(100);
            grants.Property(g => g.Role).HasColumnName("role").HasMaxLength(100).IsRequired();
            grants.Property(g => g.Permission).HasColumnName("permission").HasMaxLength(100).IsRequired();
            grants.HasKey(GrantsForeignKey, nameof(PermissionGrant.Role), nameof(PermissionGrant.Permission));
        });
        builder.Navigation(m => m.Grants).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(m => m.Roles);
    }
}
