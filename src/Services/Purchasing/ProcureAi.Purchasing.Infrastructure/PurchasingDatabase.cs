using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.Purchasing.Infrastructure;

/// <summary>The migration entry point. Nothing migrates at normal startup: the Api --migrate flag calls this and exits.</summary>
public static class PurchasingDatabase
{
    /// <summary>Applies pending migrations using the DbContext registered in the container.</summary>
    public static async Task MigratePurchasingDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PurchasingDbContext>().Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Lets dotnet ef migrations add build the context without a running host or a real database.</summary>
internal sealed class PurchasingDbContextFactory : IDesignTimeDbContextFactory<PurchasingDbContext>
{
    public PurchasingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PurchasingDbContext>().UseNpgsql("Host=localhost;Database=purchasingdb").Options);
}
