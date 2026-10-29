using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.Assistant.Infrastructure.Persistence;

/// <summary>The migration entry point. Nothing migrates at normal startup: the Api's --migrate flag calls this and exits.</summary>
public static class AssistantDatabase
{
    /// <summary>Applies pending migrations using the DbContext registered in the container.</summary>
    public static async Task MigrateAssistantDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AssistantDbContext>().Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Lets dotnet ef migrations add build the context without a running host or a real database.</summary>
internal sealed class AssistantDbContextFactory : IDesignTimeDbContextFactory<AssistantDbContext>
{
    public AssistantDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AssistantDbContext>()
            .UseNpgsql("Host=localhost;Database=assistantdb", npgsql => npgsql.UseVector())
            .Options);
}
