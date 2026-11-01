using Microsoft.Extensions.Configuration;
using Npgsql;
using ProcureAi.BuildingBlocks.Web.Options;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

public sealed class PostgresPoolingTests
{
    private const string Name = "requestsdb";

    private static NpgsqlConnectionStringBuilder Resolve(Dictionary<string, string?> settings)
    {
        var builder = new ConfigurationManager();
        builder.AddInMemoryCollection(settings);
        PostgresPooling.ApplyMaxPoolSize(builder, builder, Name);
        // A data source built from the string the service hands to EF Core, Wolverine, Marten and the health checks: the cap travels with the string.
        using var dataSource = NpgsqlDataSource.Create(builder.GetConnectionString(Name)!);
        return new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
    }

    [Fact]
    public void Data_source_uses_configured_max_pool_size()
    {
        var settings = new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{Name}"] = "Host=localhost;Database=requestsdb;Username=u;Password=p",
            ["Database:MaxPoolSize"] = "7",
        };

        Resolve(settings).MaxPoolSize.ShouldBe(7);
    }

    [Fact]
    public void Data_source_uses_the_default_pool_size_without_configuration()
    {
        var settings = new Dictionary<string, string?> { [$"ConnectionStrings:{Name}"] = "Host=localhost;Database=requestsdb" };

        Resolve(settings).MaxPoolSize.ShouldBe(new DatabaseOptions().MaxPoolSize);
        new DatabaseOptions().MaxPoolSize.ShouldBe(10);
    }
}
