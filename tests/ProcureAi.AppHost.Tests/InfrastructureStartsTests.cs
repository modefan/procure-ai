using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using Shouldly;

namespace ProcureAi.AppHost.Tests;

[Collection(InfrastructureCollection.Name)]
public sealed class InfrastructureStartsTests(AppHostFixture fixture)
{
    private static readonly string[] Infrastructure = ["postgres", "mongo", "redis", "rabbitmq", "keycloak", "mailpit"];

    // resource name -> section name in the Resources configuration
    private static readonly Dictionary<string, string> Containers = new()
    {
        ["postgres"] = "Postgres",
        ["mongo"] = "Mongo",
        ["redis"] = "Redis",
        ["rabbitmq"] = "RabbitMq",
        ["keycloak"] = "Keycloak",
        ["mailpit"] = "Mailpit",
        ["debezium"] = "Debezium",
    };

    [Fact]
    public async Task AllInfrastructureResourcesBecomeHealthy()
    {
        foreach (var resource in Infrastructure)
        {
            await Should.NotThrowAsync(() => fixture.WaitHealthyAsync(resource), $"{resource} did not become healthy");
        }
    }

    [Fact]
    public async Task Containers_have_memory_limits()
    {
        foreach (var (resource, sectionName) in Containers)
        {
            var section = fixture.Limits.GetSection(sectionName);
            var expectedMemory = section.GetValue<long>("MemoryMb") * 1024 * 1024;
            var expectedNanoCpus = (long)(section.GetValue<double>("Cpus") * 1_000_000_000);

            var id = await fixture.ContainerIdAsync(resource);
            var (memory, nanoCpus) = await InspectAsync(id);

            memory.ShouldBe(expectedMemory, $"{resource} memory limit");
            nanoCpus.ShouldBe(expectedNanoCpus, $"{resource} CPU limit");
        }
    }

    [Fact]
    public async Task Postgres_has_pgaudit_and_logical_wal()
    {
        await fixture.WaitHealthyAsync("postgres");
        await fixture.WaitHealthyAsync("requestsdb");
        var connectionString = await fixture.App.GetConnectionStringAsync("requestsdb", TestContext.Current.CancellationToken);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var cmd = new NpgsqlCommand("SHOW wal_level", connection))
        {
            ((string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("logical");
        }

        await using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS pgaudit", connection))
        {
            await Should.NotThrowAsync(() => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_publication WHERE pubname = 'dbz_publication'", connection))
        {
            ((long?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Mongo_is_a_single_node_replica_set()
    {
        await fixture.WaitHealthyAsync("mongo");
        var settings = MongoClientSettings.FromConnectionString(await fixture.App.GetConnectionStringAsync("mongo", TestContext.Current.CancellationToken));
        settings.DirectConnection = true;
        var admin = new MongoClient(settings).GetDatabase("admin");

        // The AppHost initiates the set once the container is ready; allow a little time for it.
        BsonDocument? status = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                status = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetStatus", 1), cancellationToken: TestContext.Current.CancellationToken);
                if (status["myState"].AsInt32 == 1) break;
            }
            catch (MongoCommandException) { }
            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        status.ShouldNotBeNull();
        status["set"].AsString.ShouldBe("rs0");
        status["myState"].AsInt32.ShouldBe(1, "the single member should be PRIMARY");
    }

    [Fact]
    public async Task Debezium_is_healthy()
    {
        await fixture.WaitHealthyAsync("debezium");
        using var http = fixture.App.CreateHttpClient("debezium", "http");
        using var response = await http.GetAsync("/q/health", TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        doc.RootElement.GetProperty("status").GetString().ShouldBe("UP");
    }

    [Fact]
    public async Task Cdc_exchange_exists_before_rabbitmq_reports_healthy()
    {
        await fixture.WaitHealthyAsync("rabbitmq");
        var rabbitmq = fixture.App.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<RabbitMQServerResource>().Single();
        var user = await rabbitmq.UserNameReference.GetValueAsync(TestContext.Current.CancellationToken);
        var password = await rabbitmq.PasswordParameter.GetValueAsync(TestContext.Current.CancellationToken);

        using var http = fixture.App.CreateHttpClient("rabbitmq", "management");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}")));
        using var response = await http.GetAsync("/api/exchanges/%2F/cdc", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue($"GET exchange cdc returned {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        doc.RootElement.GetProperty("type").GetString().ShouldBe("topic");
        doc.RootElement.GetProperty("durable").GetBoolean().ShouldBeTrue();
    }

    private static async Task<(long Memory, long NanoCpus)> InspectAsync(string containerId)
    {
        var psi = new ProcessStartInfo("docker", ["inspect", "--format", "{{.HostConfig.Memory}} {{.HostConfig.NanoCpus}}", containerId])
        {
            RedirectStandardOutput = true
        };
        using var process = Process.Start(psi)!;
        var output = (await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).Trim().Split(' ');
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        process.ExitCode.ShouldBe(0);
        return (long.Parse(output[0]), long.Parse(output[1]));
    }
}
