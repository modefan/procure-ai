using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>The demo seed of the real <c>--migrate</c> step, and the change data capture of the Access tables.</summary>
[Collection(AccessInfraCollection.Name)]
public sealed class SeedAndCdcTests(AccessInfraFixture infra)
{
    private static readonly TimeSpan CdcTimeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seed_is_idempotent_when_migrate_runs_twice()
    {
        var database = await infra.CreateEmptyDatabaseAsync($"seed_{Guid.NewGuid():N}");

        await MigrateProcess.RunAsync(database, infra.RabbitConnectionString, infra.RedisConnectionString, demoSeed: true);
        var first = await CountsAsync(database);
        await MigrateProcess.RunAsync(database, infra.RabbitConnectionString, infra.RedisConnectionString, demoSeed: true);
        var second = await CountsAsync(database);

        first.ShouldBe(second);
        first["permission_matrices"].ShouldBe(3, "acme, globex and demo");
        first["permission_grants"].ShouldBe(3 * 47);
        first["departments"].ShouldBe(3);
        first["cost_centers"].ShouldBe(3);
        first["tenant_users"].ShouldBe(6);
    }

    [Fact]
    public async Task Seed_does_not_run_when_demo_seed_is_disabled()
    {
        var database = await infra.CreateEmptyDatabaseAsync($"noseed_{Guid.NewGuid():N}");

        await MigrateProcess.RunAsync(database, infra.RabbitConnectionString, infra.RedisConnectionString, demoSeed: false);

        var counts = await CountsAsync(database);
        counts["permission_matrices"].ShouldBe(0);
        counts["tenant_users"].ShouldBe(0);
    }

    [Fact]
    public async Task Seeded_organisation_matches_the_demo_users()
    {
        await using var connection = new NpgsqlConnection(infra.DbConnectionString);
        await connection.OpenAsync(Ct);

        (await ScalarAsync(connection, "SELECT manager_user_id FROM cost_centers WHERE tenant_id='acme' AND code='IT-200'")).ShouldBe(DemoUsers.Maria);
        (await ScalarAsync(connection, "SELECT head_user_id FROM departments WHERE tenant_id='acme' AND code='MK'")).ShouldBe(DemoUsers.Dept);
        (await ScalarAsync(connection, "SELECT head_user_id FROM departments WHERE tenant_id='globex' AND code='OPS'")).ShouldBeNull();
        (await ScalarAsync(connection, "SELECT approval_limit::text FROM tenant_users WHERE tenant_id='acme' AND user_id='" + DemoUsers.Thandi + "'")).ShouldBe("2000000.00");
        (await ScalarAsync(connection, "SELECT is_finance_approver::text FROM tenant_users WHERE tenant_id='acme' AND user_id='" + DemoUsers.Thandi + "'")).ShouldBe("true");
    }

    [Fact]
    public async Task Cdc_captures_tenant_users_change_with_audit_row()
    {
        await infra.EnsureDebeziumAsync();
        await using var factory = new AccessApiFactory(infra);
        using var client = factory.CreateClient();
        await using var cdc = await CdcListener.StartAsync(infra.RabbitConnectionString);
        var admin = TestAuth.CreateToken(DemoUsers.Admin, "acme", "admin");

        var list = new HttpRequestMessage(HttpMethod.Get, "/api/v1/access/users");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        var users = (await (await client.SendAsync(list, Ct)).Content.ReadFromJsonAsync<List<UserDto>>(Ct))!;
        var maria = users.Single(u => u.UserId == DemoUsers.Maria);
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/access/users/{DemoUsers.Maria}/approval-limit")
        {
            Content = JsonContent.Create(new { amount = 250_000m }),
        };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        put.Headers.TryAddWithoutValidation("If-Match", $"\"{maria.Version}\"");
        (await client.SendAsync(put, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var message = await cdc.WaitForAsync(
            m => m.RoutingKey == "accessdb.public.tenant_users" && m.Operation == "u"
                 && m.After.GetProperty("user_id").GetString() == DemoUsers.Maria,
            CdcTimeout);
        message.After.GetProperty("approval_limit").ValueKind.ShouldNotBe(System.Text.Json.JsonValueKind.Null);
        await using var db = new Infrastructure.AccessDbContext(
            new DbContextOptionsBuilder<Infrastructure.AccessDbContext>().UseNpgsql(infra.DbConnectionString).Options);
        var audit = await db.Set<AuditEntry>()
            .Where(e => e.Table == "tenant_users" && e.PrimaryKey == "acme," + DemoUsers.Maria && e.Operation == AuditOperation.Update)
            .OrderByDescending(e => e.OccurredAt).FirstAsync(Ct);
        audit.TransactionId.ShouldBe(message.TransactionId, "the interceptor row and the change share a transaction");
        audit.UserId.ShouldBe(DemoUsers.Admin);
        audit.TenantId.ShouldBe("acme");
        audit.Changes.ShouldContain("approval_limit");
        audit.Changes.ShouldNotContain("cost_center_codes", Case.Sensitive, "unchanged text[] columns are not reported as changed");
        audit.Changes.ShouldNotContain("\"roles\"", Case.Sensitive, "unchanged text[] columns are not reported as changed");
    }

    private static async Task<string?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(Ct) is string text ? text : null;
    }

    private static async Task<Dictionary<string, long>> CountsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        var counts = new Dictionary<string, long>();
        foreach (var table in new[] { "permission_matrices", "permission_grants", "departments", "cost_centers", "tenant_users" })
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
            counts[table] = (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        return counts;
    }
}
