using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Requests.Infrastructure;
using Shouldly;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>The API's <c>--migrate</c> flag: the only thing that changes the schema outside Development.</summary>
[Collection(InfraCollection.Name)]
public sealed class MigrateTests(InfraFixture infra)
{
    [Fact]
    public async Task Migrate_flag_creates_the_schema_and_the_message_store_then_exits()
    {
        var database = await infra.CreateEmptyDatabaseAsync($"migrate_{Guid.NewGuid():N}");
        (await TablesAsync(database)).ShouldBeEmpty("a new database starts empty");

        await ApiMigrateProcess.RunAsync(
            "ProcureAi.Requests.Api.dll", "requestsdb", database, infra.RabbitConnectionString, infra.RedisConnectionString, demoSeed: false);

        var tables = await TablesAsync(database);
        tables.ShouldContain("public.purchase_requests");
        tables.ShouldContain("public.tenant_sequences");
        tables.ShouldContain("wolverine.wolverine_outgoing_envelopes");
        tables.ShouldContain("wolverine.wolverine_incoming_envelopes");
    }

    [Fact]
    public async Task Request_numbers_keep_counting_after_the_move_to_tenant_sequences()
    {
        var database = await infra.CreateEmptyDatabaseAsync($"sequences_{Guid.NewGuid():N}");
        await using var db = new RequestsDbContext(new DbContextOptionsBuilder<RequestsDbContext>().UseNpgsql(database).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261027060811_FollowApprovalSaga", TestContext.Current.CancellationToken);
        await ExecuteAsync(
            database, "INSERT INTO request_number_counters (tenant_id, last_value) VALUES ('acme', 1042), ('globex', 7)");

        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);

        (await TablesAsync(database)).ShouldNotContain("public.request_number_counters");
        var rows = await RowsAsync(database, "SELECT tenant_id || ':' || name || ':' || value FROM tenant_sequences ORDER BY 1");
        rows.ShouldBe(["acme:PR:1042", "globex:PR:7"]);
        var next = await new TenantSequence<RequestsDbContext>(db).NextAsync("acme", "PR", TestContext.Current.CancellationToken);
        next.ShouldBe(1043);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> RowsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<List<string>> TablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT table_schema || '.' || table_name FROM information_schema.tables WHERE table_schema IN ('public', 'wolverine')", connection);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
