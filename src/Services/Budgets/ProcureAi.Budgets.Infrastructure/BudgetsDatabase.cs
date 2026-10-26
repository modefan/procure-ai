using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.Budgets.Infrastructure;

/// <summary>The migration entry point. Nothing migrates at normal startup: the Api --migrate flag calls this and exits.</summary>
public static class BudgetsDatabase
{
    /// <summary>Applies pending migrations using the DbContext registered in the container.</summary>
    public static async Task MigrateBudgetsDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BudgetsDbContext>().Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Lets dotnet ef migrations add build the context without a running host or a real database.</summary>
internal sealed class BudgetsDbContextFactory : IDesignTimeDbContextFactory<BudgetsDbContext>
{
    public BudgetsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BudgetsDbContext>().UseNpgsql("Host=localhost;Database=budgetsdb").Options);
}
