using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ProcureAi.Approvals.Application.Dtos;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Contracts;
using Npgsql;
using Shouldly;
using static ProcureAi.Approvals.IntegrationTests.ApprovalsApp;

namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>
/// Plan 07 Task 5c: <c>GET requests/{requestId}</c> (the approval chain) is scoped like the request in Requests: its requester, the
/// people of its cost center (Access), the tenant admin (<c>admin.authorization</c>) and the admin key, plus the approvers of its
/// steps. Anyone else of the tenant gets 404, also after the saga is gone and from the 1-second GET cache.
/// </summary>
[Collection(ApprovalsCollection.Name)]
public sealed class ChainScopeTests(ApprovalsApp app)
{
    private const string Base = "/api/v1/approvals";

    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApprovalsInstance Main => app.Main;

    /// <summary>Tom's IT-200 request of R 30 000 (step 1 Maria, step 2 the department head). Alice works for CC-100, Mia for IT-200.</summary>
    private async Task<(string Tenant, Guid Id, List<Domain.ApprovalStep> Steps)> TomsChainAsync()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        app.Access.SetDisplayName(tenant, DemoUsers.Tom, "Tom Mokoena");
        app.Access.SetCostCenters(tenant, "alice", "CC-100");
        app.Access.SetCostCenters(tenant, "mia", CostCenter);
        app.Access.SetCostCenters(tenant, "ada");
        var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
        await Main.InvokeAsync(submitted, tenant, DemoUsers.Tom);
        await Main.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Tom);
        return (tenant, submitted.RequestId, await Main.StepsAsync(submitted.RequestId));
    }

    private async Task<HttpResponseMessage> ChainAsync(HttpClient client, Guid id, string tenant, string? user, string role = "requester")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/requests/{id}");
        if (user is null)
        {
            request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
            request.Headers.Add("X-Tenant-Id", tenant);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateToken(user, tenant, role));
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Another_cost_centers_chain_is_404_and_in_scope_callers_read_it()
    {
        var (tenant, id, steps) = await TomsChainAsync();
        using var client = Main.CreateClient();

        using var alice = await ChainAsync(client, id, tenant, "alice");
        alice.StatusCode.ShouldBe(HttpStatusCode.NotFound, "Alice acts for CC-100 only");
        var body = await alice.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(steps[0].Id.ToString());
        body.ShouldNotContain("Tom Mokoena");

        foreach (var (who, user, role) in new (string, string?, string)[]
                 {
                     ("the requester", DemoUsers.Tom, "requester"),
                     ("the step 1 approver", DemoUsers.Maria, "manager"),
                     ("a member of IT-200", "mia", "requester"),
                     ("the tenant admin", "ada", "admin"),
                     ("the admin key", null, ""),
                 })
        {
            using var response = await ChainAsync(client, id, tenant, user, role);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{who}: {await response.Content.ReadAsStringAsync(Ct)}");
            (await response.Content.ReadFromJsonAsync<ApprovalChainDto>(Ct))!.RequestId.ShouldBe(id);
        }
    }

    [Fact]
    public async Task A_cached_chain_is_never_served_to_an_out_of_scope_caller()
    {
        var (tenant, id, _) = await TomsChainAsync();
        using var client = Main.CreateClient();

        // Tom's 200 and the admin key's 200 are cached for a second; Alice asks inside that second.
        using var tom = await ChainAsync(client, id, tenant, DemoUsers.Tom);
        using var key = await ChainAsync(client, id, tenant, null);
        using var alice = await ChainAsync(client, id, tenant, "alice");

        tom.StatusCode.ShouldBe(HttpStatusCode.OK);
        key.StatusCode.ShouldBe(HttpStatusCode.OK);
        alice.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task After_the_saga_is_gone_the_steps_keep_the_scope()
    {
        var (tenant, id, steps) = await TomsChainAsync();
        await Main.InvokeAsync(new RejectStep(id, steps[0].Id, steps[0].Version, "Too expensive"), tenant, DemoUsers.Maria);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        using var client = Main.CreateClient();

        using var alice = await ChainAsync(client, id, tenant, "alice");
        using var mia = await ChainAsync(client, id, tenant, "mia");

        alice.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        mia.StatusCode.ShouldBe(HttpStatusCode.OK, "the step keeps the request's cost center");
        (await Main.StepsAsync(id)).ShouldAllBe(s => s.CostCenterCode == CostCenter);
    }

    [Fact]
    public async Task A_chain_without_a_stored_cost_center_fails_closed_for_a_colleague()
    {
        // A step planned before the migration kept no cost center, and its saga is gone: only the requester and the approvers can
        // be told, so a colleague of IT-200 who is no approver gets 404 (narrower, never wider).
        var (tenant, id, steps) = await TomsChainAsync();
        await Main.InvokeAsync(new RejectStep(id, steps[0].Id, steps[0].Version, "Too expensive"), tenant, DemoUsers.Maria);
        await Main.WaitForSagaAsync(id, saga => saga is null, EventTimeout);
        await using (var connection = new NpgsqlConnection(Main.DbConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("UPDATE approval_steps SET cost_center_code = '' WHERE request_id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBeGreaterThan(0);
        }

        using var client = Main.CreateClient();
        using var mia = await ChainAsync(client, id, tenant, "mia");
        using var tom = await ChainAsync(client, id, tenant, DemoUsers.Tom);
        using var maria = await ChainAsync(client, id, tenant, DemoUsers.Maria, "manager");

        mia.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        tom.StatusCode.ShouldBe(HttpStatusCode.OK);
        maria.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_approver_of_a_step_reads_the_chain_without_the_cost_center_in_Access()
    {
        // Maria approves step 1 but Access (now) assigns her only CC-100: the approver clause alone lets her read the chain.
        var (tenant, id, _) = await TomsChainAsync();
        app.Access.SetCostCenters(tenant, DemoUsers.Maria, "CC-100");
        using var client = Main.CreateClient();

        using var maria = await ChainAsync(client, id, tenant, DemoUsers.Maria, "manager");

        maria.StatusCode.ShouldBe(HttpStatusCode.OK, await maria.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_reads_by_in_scope_and_out_of_scope_callers_are_not_coalesced()
    {
        var (tenant, id, _) = await TomsChainAsync();
        using var client = Main.CreateClient();

        var reads = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => ChainAsync(client, id, tenant, i % 2 == 0 ? DemoUsers.Tom : "alice")));

        for (var i = 0; i < reads.Length; i++)
        {
            reads[i].StatusCode.ShouldBe(i % 2 == 0 ? HttpStatusCode.OK : HttpStatusCode.NotFound, $"read {i}");
            reads[i].Dispose();
        }
    }
}
