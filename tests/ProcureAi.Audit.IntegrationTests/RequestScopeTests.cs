using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Requests;
using Shouldly;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// Plan 07 Task 5c (review I1): the per-request history and data changes and the activity feed follow the request's caller scope, the
/// Requests read rule: the requester, the people of its cost center (Access), the tenant admin (<c>admin.authorization</c>) and the
/// admin key. Anyone else gets 404, does not see the request in the feed, and the read fails closed when Audit cannot tell whose
/// request it is.
/// </summary>
[Collection(AuditCollection.Name)]
public sealed class RequestScopeTests(AuditApp app)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Activity = "/api/v1/audit/activity";
    private const string Tom = DemoUsers.Tom;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string History(Guid id) => $"/api/v1/audit/requests/{id}/history";

    private static string DataChanges(Guid id) => $"/api/v1/audit/requests/{id}/data-changes";

    /// <summary>A tenant where Tom submitted an IT-200 request (and Maria was asked to approve it); Alice works for CC-100, Mia for IT-200.</summary>
    private async Task<(string Tenant, Guid Id)> TomsRequestAsync()
    {
        var tenant = AuditApp.NewTenant();
        app.Access.SetCostCenters(tenant, "alice", "CC-100");
        app.Access.SetCostCenters(tenant, "mia", "IT-200");
        app.Access.SetCostCenters(tenant, "ada");
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await app.PublishAsync(new RequestSubmitted(id, tenant, "PR-7", "Tom's laptop", Tom, "IT-200", 30_000m, now.AddMinutes(-2), "SUP-1", []),
            Exchanges.Requests, tenant, Tom);
        await app.PublishAsync(new ApprovalRequested(id, tenant, "PR-7", Guid.NewGuid(), 1, "Manager", [DemoUsers.Maria], now.AddDays(2), now.AddMinutes(-1)),
            Exchanges.Approvals, tenant, Tom);
        await app.WaitForStoredAsync(tenant, s => s.Count == 2);
        await StoreRowAsync(tenant, id, Tom, "IT-200");
        return (tenant, id);
    }

    /// <summary>A captured <c>purchase_requests</c> row of the request, as Debezium would have delivered it.</summary>
    private async Task StoreRowAsync(string tenant, Guid id, string requester, string costCenter, DateTimeOffset? at = null)
    {
        var after = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = id.ToString(), ["tenant_id"] = tenant, ["title"] = "Tom's laptop", ["requester_id"] = requester, ["cost_center_code"] = costCenter,
        });
        var change = new DataChange(
            $"requestsdb:{Guid.NewGuid():N}:purchase_requests:{id}", AuditApp.RequestsDb, "purchase_requests", id.ToString(), id, tenant, "Insert",
            Random.Shared.NextInt64(1, long.MaxValue), at ?? DateTimeOffset.UtcNow, null, after, Attribution.OutsideApp);
        await using var session = app.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Insert(change);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string tenant, string? user, string role = "requester") =>
        await app.Client.SendAsync(
            user is null ? AuditApp.GetWithApiKey(url, tenant) : AuditApp.Get(url, TestAuth.CreateToken(user, tenant, role)), Ct);

    private async Task<List<ActivityDto>> FeedAsync(string tenant, string? user, string role = "requester", int limit = 20)
    {
        using var response = await GetAsync($"{Activity}?limit={limit}", tenant, user, role);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<List<ActivityDto>>(Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Another_cost_centers_request_is_404_on_history_and_data_changes()
    {
        var (tenant, id) = await TomsRequestAsync();

        foreach (var url in new[] { History(id), DataChanges(id) })
        {
            using var response = await GetAsync(url, tenant, "alice");
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{url}: Alice acts for CC-100 only");
            (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Tom's laptop");
        }
    }

    [Fact]
    public async Task Requester_cost_center_member_tenant_admin_and_admin_key_read_both()
    {
        var (tenant, id) = await TomsRequestAsync();
        var readers = new (string Who, string? User, string Role)[]
        {
            ("the requester", Tom, "requester"),
            ("a member of IT-200", "mia", "requester"),
            ("the tenant admin (admin.authorization, no cost centers)", "ada", "admin"),
            ("the admin key", null, ""),
        };

        foreach (var url in new[] { History(id), DataChanges(id) })
        {
            foreach (var (who, user, role) in readers)
            {
                using var response = await GetAsync(url, tenant, user, role);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{url} as {who}: {await response.Content.ReadAsStringAsync(Ct)}");
            }
        }
    }

    [Fact]
    public async Task Activity_lists_the_request_only_for_callers_in_scope()
    {
        var (tenant, id) = await TomsRequestAsync();
        // Alice's own request, so her feed is not empty for a wrong reason.
        var own = Guid.NewGuid();
        await app.PublishAsync(new RequestSubmitted(own, tenant, "PR-8", "Alice's chair", "alice", "CC-100", 2_000m, DateTimeOffset.UtcNow, "SUP-1", []),
            Exchanges.Requests, tenant, "alice");
        await app.WaitForStoredAsync(tenant, s => s.Count == 3);

        var alice = await FeedAsync(tenant, "alice");
        alice.Select(a => a.RequestId).ShouldBe([own]);
        alice.ShouldAllBe(a => !a.Summary.Contains("Tom's laptop"));

        foreach (var (user, role) in new (string?, string)[] { (Tom, "requester"), ("mia", "requester") })
        {
            (await FeedAsync(tenant, user, role)).Where(a => a.RequestId == id).Select(a => a.EventType)
                .ShouldBe(["ApprovalRequested", "RequestSubmitted"], user);
        }

        (await FeedAsync(tenant, "ada", "admin")).Count.ShouldBe(3, "the tenant admin sees the whole tenant");
        (await FeedAsync(tenant, null)).Count.ShouldBe(3, "the admin key sees the whole tenant");
    }

    [Fact]
    public async Task Out_of_scope_events_do_not_use_up_the_activity_limit()
    {
        var (tenant, _) = await TomsRequestAsync();
        var own = Guid.NewGuid();
        // Alice's request is the oldest; Tom's (two events) and four more of his IT-200 requests are newer.
        await app.PublishAsync(new RequestSubmitted(own, tenant, "PR-1", "Alice's chair", "alice", "CC-100", 2_000m, DateTimeOffset.UtcNow.AddHours(-1), "SUP-1", []),
            Exchanges.Requests, tenant, "alice");
        for (var i = 0; i < 4; i++)
        {
            await app.PublishAsync(new RequestSubmitted(Guid.NewGuid(), tenant, $"PR-{20 + i}", "Tom's", Tom, "IT-200", 1m, DateTimeOffset.UtcNow, "SUP-1", []),
                Exchanges.Requests, tenant, Tom);
        }

        await app.WaitForStoredAsync(tenant, s => s.Count == 7);

        (await FeedAsync(tenant, "alice", limit: 2)).Select(a => a.RequestId).ShouldBe([own], "filtered before the limit, not after");
        (await FeedAsync(tenant, null, limit: 2)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_cached_answer_is_never_served_to_an_out_of_scope_caller()
    {
        var (tenant, id) = await TomsRequestAsync();

        foreach (var url in new[] { History(id), DataChanges(id) })
        {
            using var tom = await GetAsync(url, tenant, Tom);
            using var key = await GetAsync(url, tenant, null);
            using var alice = await GetAsync(url, tenant, "alice");

            tom.StatusCode.ShouldBe(HttpStatusCode.OK, url);
            key.StatusCode.ShouldBe(HttpStatusCode.OK, url);
            alice.StatusCode.ShouldBe(HttpStatusCode.NotFound, url);
        }

        // The feed: the admin key's and Tom's cached feeds hold Tom's request; Alice's must not.
        (await FeedAsync(tenant, null)).ShouldContain(a => a.RequestId == id);
        (await FeedAsync(tenant, Tom)).ShouldContain(a => a.RequestId == id);
        (await FeedAsync(tenant, "alice")).ShouldNotContain(a => a.RequestId == id);
    }

    [Fact]
    public async Task Unknown_owner_fails_closed()
    {
        // Audit has an approval event of the request but neither its RequestSubmitted nor a captured row: nobody can tell whose it is.
        var tenant = AuditApp.NewTenant();
        app.Access.SetCostCenters(tenant, "mia", "IT-200");
        var id = Guid.NewGuid();
        await app.PublishAsync(new ApprovalRequested(id, tenant, "PR-9", Guid.NewGuid(), 1, "Manager", [DemoUsers.Maria], DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow),
            Exchanges.Approvals, tenant, Tom);
        await app.WaitForStoredAsync(tenant, s => s.Count == 1);

        using var history = await GetAsync(History(id), tenant, "mia");
        history.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FeedAsync(tenant, "mia")).ShouldBeEmpty();
        using var asKey = await GetAsync(History(id), tenant, null);
        asKey.StatusCode.ShouldBe(HttpStatusCode.OK, "the admin key needs no owner");
    }

    [Fact]
    public async Task A_draft_is_scoped_by_its_newest_captured_row()
    {
        // Never submitted: no RequestSubmitted. The row moved from IT-200 to CC-100 while a draft; the newest row decides.
        var tenant = AuditApp.NewTenant();
        app.Access.SetCostCenters(tenant, "alice", "CC-100");
        app.Access.SetCostCenters(tenant, "mia", "IT-200");
        var id = Guid.NewGuid();
        await StoreRowAsync(tenant, id, Tom, "IT-200", DateTimeOffset.UtcNow.AddMinutes(-5));
        await StoreRowAsync(tenant, id, Tom, "CC-100");

        using var alice = await GetAsync(DataChanges(id), tenant, "alice");
        using var mia = await GetAsync(DataChanges(id), tenant, "mia");
        using var tom = await GetAsync(DataChanges(id), tenant, Tom);

        alice.StatusCode.ShouldBe(HttpStatusCode.OK, await alice.Content.ReadAsStringAsync(Ct));
        (await alice.Content.ReadFromJsonAsync<PagedResult<DataChangeDto>>(Json, Ct))!.Total.ShouldBe(2);
        mia.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the draft is no longer on IT-200");
        tom.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Access_down_fails_closed_for_a_user()
    {
        var (tenant, id) = await TomsRequestAsync();

        app.Access.Down = true;
        try
        {
            foreach (var url in new[] { History(id), DataChanges(id), Activity })
            {
                using var response = await GetAsync(url, tenant, "mia");
                response.IsSuccessStatusCode.ShouldBeFalse($"{url}: the scope cannot be told without Access");
                (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Tom's laptop");
            }
        }
        finally
        {
            app.Access.Down = false;
        }
    }
}
