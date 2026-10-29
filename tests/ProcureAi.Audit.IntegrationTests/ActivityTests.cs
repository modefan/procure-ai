using System.Net;
using System.Net.Http.Json;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.Contracts;
using ProcureAi.Testing;
using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Invoicing;
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

        using var response = await app.Client.SendAsync(AuditApp.Get(Activity, TestAuth.CreateToken("fiona", acme, "finance")), Ct);

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

    private async Task<List<ActivityDto>> ReadAsAsync(string tenant, string role)
    {
        using var response = await app.Client.SendAsync(AuditApp.Get(Activity, TestAuth.CreateToken($"{role}-user", tenant, role)), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<List<ActivityDto>>(Ct)).ShouldNotBeNull();
    }

    /// <summary>
    /// Final review ruling: the feed is tenant-wide, but budget amounts and invoice and payment events belong to people who may see
    /// them. A requester gets the request events only; finance (budgets.manage, invoices.release-payment) and the admin key get all.
    /// </summary>
    [Fact]
    public async Task Budget_invoice_and_payment_events_are_for_finance_not_for_a_requester()
    {
        var tenant = AuditApp.NewTenant();
        var request = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await app.PublishAsync(new RequestSubmitted(request, tenant, "PR-7", "Laptop", Tom, "FIN-100", 30_000m, now.AddMinutes(-10), "SUP-1", []),
            Exchanges.Requests, tenant, Tom);
        await app.PublishAsync(new BudgetReserved(request, tenant, "FIN-100", "FY2027", 30_000m, now.AddMinutes(-9)), Exchanges.Budgets, tenant, Tom);
        await app.PublishAsync(new BudgetAmountSet(tenant, "FIN-100", "FY2027", 150_000m, 3, now.AddMinutes(-8)), Exchanges.Budgets, tenant, "fiona");
        await app.PublishAsync(
            new PaymentReleased(Guid.NewGuid(), "PAY-1", Guid.NewGuid(), Guid.NewGuid(), request, tenant, 115m, [], "fiona", now.AddMinutes(-7)),
            Exchanges.Invoicing, tenant, "fiona");
        await app.PublishAsync(new InvoiceMatched(Guid.NewGuid(), Guid.NewGuid(), request, tenant, now.AddMinutes(-6)), Exchanges.Invoicing, tenant, "fiona");
        await app.WaitForStoredAsync(tenant, s => s.Count == 5);

        var requester = await ReadAsAsync(tenant, "requester");
        var finance = await ReadAsAsync(tenant, "finance");
        var admin = await ReadAsync(tenant);

        requester.Select(f => f.EventType).ShouldBe(["RequestSubmitted"]);
        requester.Any(f => f.Summary.Contains("150")).ShouldBeFalse("a budget amount is not for every requests.read user");
        finance.Select(f => f.EventType).ShouldBe(["InvoiceMatched", "PaymentReleased", "BudgetAmountSet", "BudgetReserved", "RequestSubmitted"]);
        admin.Count.ShouldBe(5);
    }

    /// <summary>The limit counts only what the caller may see: hidden events do not eat into the page.</summary>
    [Fact]
    public async Task Hidden_events_do_not_use_up_the_limit()
    {
        var tenant = AuditApp.NewTenant();
        var now = DateTimeOffset.UtcNow;
        await app.PublishAsync(new RequestSubmitted(Guid.NewGuid(), tenant, "PR-1", "Old", Tom, "IT-200", 1m, now.AddMinutes(-30), "SUP-1", []),
            Exchanges.Requests, tenant, Tom);
        for (var i = 0; i < 3; i++)
        {
            await app.PublishAsync(new BudgetReserved(Guid.NewGuid(), tenant, "IT-200", "FY2027", 1m, now.AddMinutes(-i)), Exchanges.Budgets, tenant, Tom);
        }

        await app.WaitForStoredAsync(tenant, s => s.Count == 4);
        using var response = await app.Client.SendAsync(
            AuditApp.Get(Activity + "?limit=2", TestAuth.CreateToken("req-user", tenant, "requester")), Ct);

        (await response.Content.ReadFromJsonAsync<List<ActivityDto>>(Ct))!.Select(f => f.EventType).ShouldBe(["RequestSubmitted"]);
    }
}
