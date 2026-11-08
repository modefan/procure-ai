using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProcureAi.Access.Domain;

namespace ProcureAi.Access.Infrastructure.Configurations;

internal sealed class SystemSettingsConfiguration : IEntityTypeConfiguration<SystemSettings>
{
    public void Configure(EntityTypeBuilder<SystemSettings> builder)
    {
        builder.ToTable("system_settings", table => table.HasCheckConstraint("CK_system_settings_single_row", $"id = {SystemSettings.SingletonId}"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.Enabled).HasColumnName("enabled");
        builder.Property(s => s.Message).HasColumnName("message").HasMaxLength(SystemSettings.MaxMessageLength);
        builder.Property(s => s.ChangedBy).HasColumnName("changed_by").HasMaxLength(100).IsRequired();
        builder.Property(s => s.ChangedAt).HasColumnName("changed_at");
    }
}
