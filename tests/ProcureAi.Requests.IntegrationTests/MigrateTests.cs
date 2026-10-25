using System.Diagnostics;
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

        var start = new ProcessStartInfo("dotnet", ["exec", ApiDll(), "--migrate"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(ApiDll())!,
        };
        // Production settings: no automatic schema changes, so any table that appears came from --migrate.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ConnectionStrings__requestsdb"] = database;
        start.Environment["ConnectionStrings__rabbitmq"] = infra.RabbitConnectionString;
        start.Environment["ConnectionStrings__Redis"] = infra.RedisConnectionString;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        process.ExitCode.ShouldBe(0, (await output) + (await error));
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

    private static string ApiDll() => Path.Combine(AppContext.BaseDirectory, "ProcureAi.Requests.Api.dll");
}
