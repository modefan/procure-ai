using Npgsql;
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
        tables.ShouldContain("public.request_number_counters");
        tables.ShouldContain("wolverine.wolverine_outgoing_envelopes");
        tables.ShouldContain("wolverine.wolverine_incoming_envelopes");
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
