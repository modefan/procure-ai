using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>
/// Plan 08 Task 12b: the system-wide on/off switch. Anyone may read it (the web app asks before sign-in, the gateway for every browser
/// call); only the admin API key may change it, never a user of any role nor a service account. Each test has a database of its own, so
/// the one switch row of one test never meets another's.
/// </summary>
[Collection(AccessInfraCollection.Name)]
public sealed class SystemAvailabilityTests(AccessInfraFixture infra)
{
    private const string Path = "/api/v1/system/availability";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Roles => new(["requester", "manager", "department-head", "finance", "admin"]);

    [Fact]
    public async Task Without_a_row_the_system_is_on_and_anyone_may_ask()
    {
        var (factory, database) = await FactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<SystemAvailabilityDto>(Json, Ct)).ShouldBe(new SystemAvailabilityDto(true, null));
        (await CountAsync(database, "select count(*) from system_settings")).ShouldBe(0, "reading never writes the row");
    }

    [Fact]
    public async Task Admin_key_switches_off_and_on_and_every_change_is_audited()
    {
        var (factory, database) = await FactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateClient();

        var off = await PutAsync(client, new { enabled = false, message = "  Paused to save costs.  " });
        off.ShouldBe(new SystemAvailabilityDto(false, "Paused to save costs."));
        (await GetAsync(client)).ShouldBe(off, "the write drops the micro-cached read");

        (await PutAsync(client, new { enabled = false, message = "  Paused to save costs.  " })).ShouldBe(off, "the same PUT again is harmless");

        var on = await PutAsync(client, new { enabled = true });
        on.ShouldBe(new SystemAvailabilityDto(true, null));
        (await GetAsync(client)).ShouldBe(on);

        (await CountAsync(database, "select count(*) from system_settings")).ShouldBe(1, "one row, however often it is switched");
        (await CountAsync(database, "select count(*) from system_settings where changed_by = 'claude-admin'")).ShouldBe(1);
        (await CountAsync(database, "select count(*) from audit_entries where table_name = 'system_settings' and actor_id = 'claude-admin'"))
            .ShouldBeGreaterThanOrEqualTo(2, "the first switch (insert) and the switch back on (update) are both in the data change audit");
        (await CountAsync(database, "select count(*) from audit_entries where table_name = 'system_settings' and operation = 'Insert'")).ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task A_user_of_any_role_is_refused(string role)
    {
        var (factory, database) = await FactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, Path) { Content = JsonContent.Create(new { enabled = false }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateToken(DemoUsers.Admin, "acme", role));

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CountAsync(database, "select count(*) from system_settings")).ShouldBe(0);
    }

    [Fact]
    public async Task A_service_account_a_forged_key_and_an_anonymous_caller_are_refused()
    {
        var (factory, database) = await FactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateClient();

        using var service = new HttpRequestMessage(HttpMethod.Put, Path) { Content = JsonContent.Create(new { enabled = false }) };
        service.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateServiceToken(AccessApiFactory.InternalClientId));
        using var forged = new HttpRequestMessage(HttpMethod.Put, Path) { Content = JsonContent.Create(new { enabled = false }) };
        forged.Headers.Add("X-Api-Key", "not-the-admin-key-but-long-enough-0123456789");
        using var anonymous = new HttpRequestMessage(HttpMethod.Put, Path) { Content = JsonContent.Create(new { enabled = false }) };

        (await client.SendAsync(service, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.SendAsync(forged, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(anonymous, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CountAsync(database, "select count(*) from system_settings")).ShouldBe(0);
    }

    [Fact]
    public async Task Invalid_bodies_are_400()
    {
        var (factory, database) = await FactoryAsync();
        await using var _ = factory;
        using var client = factory.CreateClient();

        (await SendAsync(client, new { message = "no switch" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(client, new { enabled = false, message = new string('x', 501) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountAsync(database, "select count(*) from system_settings")).ShouldBe(0);
    }

    [Fact]
    public async Task The_demo_reset_keeps_the_switch()
    {
        var (factory, _) = await FactoryAsync(demoReset: true);
        await using var __ = factory;
        using var client = factory.CreateClient();
        await PutAsync(client, new { enabled = false, message = "Off for the night." });

        (await DemoResetProbe.ResetAsync(client, "access")).ShouldBe(HttpStatusCode.NoContent);

        (await GetAsync(client)).ShouldBe(new SystemAvailabilityDto(false, "Off for the night."));
    }

    [Fact]
    public async Task The_gateways_own_read_is_not_starved_by_a_flood_of_anonymous_reads()
    {
        // Behind the gateway every anonymous browser read comes from the gateway's address, so they share one partition. The gateway's
        // own read carries its service token and counts in that account's partition, which no header can claim.
        var (factory, _) = await FactoryAsync(new Dictionary<string, string?>
        {
            ["RateLimiting:PermitLimit"] = "3",
            ["RateLimiting:WindowSeconds"] = "3600",
        });
        await using var _ = factory;
        using var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync(Path, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var flooded = await client.GetAsync(Path, Ct))
        {
            flooded.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the anonymous partition is used up");
        }

        using (var spoofed = new HttpRequestMessage(HttpMethod.Get, Path))
        {
            spoofed.Headers.Add("X-Calling-Service", "gateway");
            spoofed.Headers.Add("X-Forwarded-For", "203.0.113.9");
            (await client.SendAsync(spoofed, Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "headers are no way out of it");
        }

        using var gateway = new HttpRequestMessage(HttpMethod.Get, Path);
        gateway.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateServiceToken("procure-internal"));
        gateway.Headers.Add("X-Calling-Service", "gateway");
        using var read = await client.SendAsync(gateway, Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(Ct));
        (await read.Content.ReadFromJsonAsync<SystemAvailabilityDto>(Json, Ct)).ShouldBe(new SystemAvailabilityDto(true, null));
    }

    private Task<(AccessApiFactory Factory, string Database)> FactoryAsync(bool demoReset = false) =>
        FactoryAsync(demoReset ? new Dictionary<string, string?> { ["DemoReset:Enabled"] = "true" } : new Dictionary<string, string?>());

    private async Task<(AccessApiFactory Factory, string Database)> FactoryAsync(Dictionary<string, string?> extra)
    {
        var database = await infra.CreateSeededDatabaseAsync($"system_{Guid.NewGuid():N}");
        var settings = new Dictionary<string, string?>(extra) { ["ConnectionStrings:accessdb"] = database };

        return (new AccessApiFactory(infra, settings: settings), database);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<SystemAvailabilityDto> PutAsync(HttpClient client, object body)
    {
        using var response = await SendAsync(client, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SystemAvailabilityDto>(Json, Ct))!;
    }

    private static async Task<SystemAvailabilityDto> GetAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Path, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SystemAvailabilityDto>(Json, Ct))!;
    }

    private static async Task<long> CountAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
