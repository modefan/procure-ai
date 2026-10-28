using System.Net;
using System.Net.Http.Json;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.Contracts;
using ProcureAi.Testing;
using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Requests;
using Shouldly;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>The recent activity feed of the dashboard: the tenant's latest business events, newest first.</summary>
[Collection(AuditCollection.Name)]
public sealed class ActivityTests(AuditApp app)
{
    private const string Activity = "/api/v1/audit/activity";
    private const string Tom = DemoUsers.Tom;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<List<ActivityDto>> ReadAsync(string tenant, string query = "")
    {
        using var response = await app.Client.SendAsync(AuditApp.GetWithApiKey(Activity + query, tenant), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<List<ActivityDto>>(Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Activity_lists_newest_first_within_tenant()
    {
        var tenant = AuditApp.NewTenant();
        var globex = AuditApp.NewTenant();
        var request = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        app.Access.SetDisplayName(tenant, Tom, "Tom Tester");
        await app.PublishAsync(new RequestSubmitted(request, tenant, "PR-7", "Laptop", Tom, "IT-200", 30_000m, now.AddMinutes(-10), "SUP-1", []),
            Exchanges.Requests, tenant, Tom);
        await app.PublishAsync(new BudgetReserved(request, tenant, "IT-200", "FY2027", 30_000m, now.AddMinutes(-9)),
            Exchanges.Budgets, tenant, Tom);
        await app.PublishAsync(new RequestSubmitted(Guid.NewGuid(), globex, "PR-9", "Secret globex", Tom, "X-1", 1m, now.AddMinutes(-1), "SUP-1", []),
            Exchanges.Requests, globex, Tom);
        await app.WaitForStoredAsync(tenant, s => s.Count == 2);
        await app.WaitForStoredAsync(globex, s => s.Count == 1);

        var feed = await ReadAsync(tenant);

        feed.Select(f => f.EventType).ShouldBe(["BudgetReserved", "RequestSubmitted"]);
        feed.ShouldAllBe(f => f.RequestId == request && f.RequestNumber == "PR-7");
        feed[1].Summary.ShouldContain("Tom Tester");
        feed[1].ActorName.ShouldBe("Tom Tester");
        feed.Any(f => f.Summary.Contains("globex", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Fact]
    public async Task Activity_needs_a_login()
    {
        using var response = await app.Client.GetAsync(Activity, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Activity_without_requests_read_is_403()
    {
        var tenant = AuditApp.NewTenant();

        using var response = await app.Client.SendAsync(AuditApp.Get(Activity, TestAuth.CreateToken("nobody-user", tenant, "nobody")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Activity_of_a_user_token_shows_only_the_users_tenant()
    {
        var acme = AuditApp.NewTenant();
        var globex = AuditApp.NewTenant();
        var now = DateTimeOffset.UtcNow;
        await app.PublishAsync(new BudgetReserved(Guid.NewGuid(), acme, "ACME-CC", "FY2027", 1m, now), Exchanges.Budgets, acme, Tom);
        await app.PublishAsync(new BudgetReserved(Guid.NewGuid(), globex, "GLOBEX-CC", "FY2027", 1m, now), Exchanges.Budgets, globex, Tom);
        await app.WaitForStoredAsync(acme, s => s.Count == 1);
        await app.WaitForStoredAsync(globex, s => s.Count == 1);

        using var response = await app.Client.SendAsync(AuditApp.Get(Activity, TestAuth.CreateToken("rita", acme, "requester")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var feed = (await response.Content.ReadFromJsonAsync<List<ActivityDto>>(Ct))!;
        feed.ShouldHaveSingleItem().Summary.ShouldContain("ACME-CC");
    }

    [Fact]
    public async Task Activity_honours_limit_and_refuses_values_outside_1_to_50()
    {
        var tenant = AuditApp.NewTenant();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            await app.PublishAsync(new BudgetReserved(Guid.NewGuid(), tenant, "IT-200", "FY2027", 1m, now.AddMinutes(-i)), Exchanges.Budgets, tenant, Tom);
        }

        await app.WaitForStoredAsync(tenant, s => s.Count == 3);

        (await ReadAsync(tenant, "?limit=2")).Count.ShouldBe(2);
        foreach (var bad in new[] { "0", "51", "-1" })
        {
            using var response = await app.Client.SendAsync(AuditApp.GetWithApiKey($"{Activity}?limit={bad}", tenant), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, bad);
        }
    }
}
