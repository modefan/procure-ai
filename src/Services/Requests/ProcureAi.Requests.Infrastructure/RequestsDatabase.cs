using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.Requests.Infrastructure;

/// <summary>
/// The migration entry point. Nothing migrates at normal startup: the Api's <c>--migrate</c> flag (Task 9)
/// calls this and exits.
/// </summary>
public static class RequestsDatabase
{
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<RequestsDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new RequestsDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Applies pending migrations using the DbContext registered in the container.</summary>
    public static async Task MigrateRequestsDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RequestsDbContext>().Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without a running host or a real database.</summary>
internal sealed class RequestsDbContextFactory : IDesignTimeDbContextFactory<RequestsDbContext>
{
    public RequestsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RequestsDbContext>().UseNpgsql("Host=localhost;Database=requestsdb").Options);
}
