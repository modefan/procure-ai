using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>One counter row per tenant and sequence name: <c>tenant_sequences(tenant_id, name, value)</c>.</summary>
public sealed class TenantSequenceRow
{
    public string TenantId { get; set; } = "";

    public string Name { get; set; } = "";

    public int Value { get; set; }
}

public static class TenantSequenceModel
{
    /// <summary>Adds the table <c>tenant_sequences</c> to the model. Not captured by Debezium: counters are not business data.</summary>
    public static ModelBuilder AddTenantSequences(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantSequenceRow>(row =>
        {
            row.ToTable("tenant_sequences");
            row.HasKey(r => new { r.TenantId, r.Name });
            row.Property(r => r.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
            row.Property(r => r.Name).HasColumnName("name").HasMaxLength(100);
            row.Property(r => r.Value).HasColumnName("value");
        });
        return modelBuilder;
    }

    /// <summary>Registers <see cref="ITenantSequence"/> on <typeparamref name="TDbContext"/> (which called <see cref="AddTenantSequences"/>).</summary>
    public static IServiceCollection AddTenantSequence<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext =>
        services.AddScoped<ITenantSequence, TenantSequence<TDbContext>>();
}
