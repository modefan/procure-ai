using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.Approvals.Infrastructure;

/// <summary>The migration entry point. Nothing migrates at normal startup: the Api --migrate flag calls this and exits.</summary>
public static class ApprovalsDatabase
{
    /// <summary>Applies pending migrations using the DbContext registered in the container.</summary>
    public static async Task MigrateApprovalsDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApprovalsDbContext>().Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Lets dotnet ef migrations add build the context without a running host or a real database.</summary>
internal sealed class ApprovalsDbContextFactory : IDesignTimeDbContextFactory<ApprovalsDbContext>
{
    public ApprovalsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ApprovalsDbContext>().UseNpgsql("Host=localhost;Database=approvalsdb").Options);
}
