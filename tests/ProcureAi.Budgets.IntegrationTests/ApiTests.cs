using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ProcureAi.Budgets.Application.Dtos;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts.Budgets;
using Shouldly;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>The HTTP API of Budgets (permissions, cost center scoping, ETags, the live check) and the change data capture.</summary>
[Collection(BudgetsCollection.Name)]
public sealed class ApiTests(BudgetsApp app)
{
    private const string Base = "/api/v1/budgets";
    private static readonly TimeSpan CdcTimeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Year => BudgetsInfraFixture.CurrentYear.ToString();

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, string? ifMatch = null, object? body = null) =>
        app.Client.SendAsync(BudgetsApp.Request(method, url, token, ifMatch, body), Ct);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    [Fact]
    public async Task Requester_sees_only_own_cost_centers()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, "CC-A", 100_000m);
        await app.Infra.CreateBudgetAsync(tenant, "CC-B", 50_000m);
        app.Access.SetCostCenters(tenant, "tom", "CC-A");
        var requester = BudgetsApp.Token(tenant, "requester", "tom");
        var finance = BudgetsApp.Token(tenant, "finance", "thandi");

        var own = await ReadAsync<List<BudgetDto>>(await SendAsync(HttpMethod.Get, $"{Base}?fiscalYear={Year}", requester));
        var other = await SendAsync(HttpMethod.Get, $"{Base}/CC-B/{Year}", requester);
        var ownOne = await SendAsync(HttpMethod.Get, $"{Base}/CC-A/{Year}", requester);
        var all = await ReadAsync<List<BudgetDto>>(await SendAsync(HttpMethod.Get, $"{Base}?fiscalYear={Year}", finance));

        own.Select(b => b.CostCenterCode).ShouldBe(["CC-A"]);
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a cost center the caller does not act for looks like one that does not exist");
        ownOne.StatusCode.ShouldBe(HttpStatusCode.OK);
        all.Select(b => b.CostCenterCode).ShouldBe(["CC-A", "CC-B"]);
    }

    [Fact]
    public async Task Reading_budgets_needs_the_permission_and_a_tenant()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, "CC-A", 1_000m);

        var anonymous = await app.Client.GetAsync($"{Base}?fiscalYear={Year}", Ct);
        var noRole = await SendAsync(HttpMethod.Get, $"{Base}?fiscalYear={Year}", BudgetsApp.Token(tenant, "nobody"));

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        noRole.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Another_tenant_never_sees_the_budget()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, "CC-A", 1_000m);

        var other = await SendAsync(HttpMethod.Get, $"{Base}/CC-A/{Year}", BudgetsApp.Token(BudgetsInfraFixture.NewTenant(), "finance"));

        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Put_changes_the_amount_and_returns_a_new_etag()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, "CC-A", 100_000m);
        var finance = BudgetsApp.Token(tenant, "finance");
        var read = await SendAsync(HttpMethod.Get, $"{Base}/CC-A/{Year}", finance);
        var budget = await ReadAsync<BudgetDto>(read);
        read.Headers.ETag!.Tag.ShouldBe($"\"{budget.Version}\"");

        var put = await SendAsync(HttpMethod.Put, $"{Base}/CC-A/{Year}", finance, ifMatch: read.Headers.ETag.Tag, body: new { amount = 150_000m });

        var updated = await ReadAsync<BudgetDto>(put);
        updated.Amount.ShouldBe(150_000m);
        updated.Available.ShouldBe(150_000m);
        updated.Version.ShouldNotBe(budget.Version);
        put.Headers.ETag!.Tag.ShouldBe($"\"{updated.Version}\"");
        (await app.GetBudgetAsync(tenant, "CC-A")).Amount.ShouldBe(150_000m, "the cached read was evicted by the write");
    }

    [Fact]
    public async Task Put_with_stale_etag_412()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, "CC-A", 100_000m);
        var finance = BudgetsApp.Token(tenant, "finance");
        var budget = await app.GetBudgetAsync(tenant, "CC-A");
        var first = await SendAsync(HttpMethod.Put, $"{Base}/CC-A/{Year}", finance, ifMatch: $"\"{budget.Version}\"", body: new { amount = 120_000m });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stale = await SendAsync(HttpMethod.Put, $"{Base}/CC-A/{Year}", finance, ifMatch: $"\"{budget.Version}\"", body: new { amount = 130_000m });
        var missing = await SendAsync(HttpMethod.Put, $"{Base}/CC-A/{Year}", finance, body: new { amount = 130_000m });

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        ((int)missing.StatusCode).ShouldBe(428);
    }

    [Fact]
    public async Task Put_needs_budgets_manage_and_cannot_go_below_what_is_reserved()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, "CC-A", 100_000m);
        var id = Guid.NewGuid();
        await app.SendAsync(new ReserveBudget(id, tenant, "CC-A", 60_000m, DateTimeOffset.UtcNow), tenant);
        await app.Events.WaitForAsync(id, e => e.Count > 0, BudgetsApp.EventTimeout);
        var budget = await app.GetBudgetAsync(tenant, "CC-A");

        var requester = await SendAsync(HttpMethod.Put, $"{Base}/CC-A/{Year}", BudgetsApp.Token(tenant, "requester"), $"\"{budget.Version}\"", new { amount = 1m });
        var tooLow = await SendAsync(HttpMethod.Put, $"{Base}/CC-A/{Year}", BudgetsApp.Token(tenant, "finance"), $"\"{budget.Version}\"", new { amount = 59_999m });

        requester.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        tooLow.StatusCode.ShouldBe((HttpStatusCode)422);
    }

    [Fact]
    public async Task Check_endpoint_reports_fits_and_available()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, "CC-A", 100_000m);
        var id = Guid.NewGuid();
        await app.SendAsync(new ReserveBudget(id, tenant, "CC-A", 30_000m, DateTimeOffset.UtcNow), tenant);
        await app.Events.WaitForAsync(id, e => e.Count > 0, BudgetsApp.EventTimeout);
        var requester = BudgetsApp.Token(tenant, "requester", "tom");

        var fits = await ReadAsync<BudgetCheckDto>(await SendAsync(HttpMethod.Get, $"{Base}/CC-A/check?amount=70000", requester));
        var tooMuch = await ReadAsync<BudgetCheckDto>(await SendAsync(HttpMethod.Get, $"{Base}/CC-A/check?amount=70000.01", requester));
        var unknown = await ReadAsync<BudgetCheckDto>(await SendAsync(HttpMethod.Get, $"{Base}/CC-Z/check?amount=1", requester));

        fits.ShouldBe(new BudgetCheckDto(true, 70_000m, Year));
        tooMuch.ShouldBe(new BudgetCheckDto(false, 70_000m, Year));
        unknown.Fits.ShouldBeFalse();
        unknown.Available.ShouldBe(0m);
    }

    [Fact]
    public async Task Check_endpoint_needs_requests_create()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, "CC-A", 1_000m);

        var admin = await SendAsync(HttpMethod.Get, $"{Base}/CC-A/check?amount=1", BudgetsApp.Token(tenant, "admin"));

        admin.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the admin role has no requests.create");
    }

    [Fact]
    public async Task Cdc_captures_reservation_with_audit_row()
    {
        await app.Infra.EnsureDebeziumAsync();
        await using var cdc = await CdcListener.StartAsync(app.Infra.RabbitConnectionString);
        var tenant = BudgetsInfraFixture.NewTenant();
        var budget = await app.Infra.CreateBudgetAsync(tenant, "CC-A", 100_000m);
        var id = Guid.NewGuid();

        await app.SendAsync(new ReserveBudget(id, tenant, "CC-A", 20_000m, DateTimeOffset.UtcNow), tenant);

        var message = await cdc.WaitForAsync(
            m => m.RoutingKey == "budgetsdb.public.budget_reservations" && m.Operation == "c"
                 && m.After.GetProperty("request_id").GetString() == id.ToString(),
            CdcTimeout);
        message.After.GetProperty("amount").ValueKind.ShouldNotBe(JsonValueKind.Null);
        var budgetChange = await cdc.WaitForAsync(
            m => m.RoutingKey == "budgetsdb.public.budgets" && m.Operation == "u" && m.After.GetProperty("tenant_id").GetString() == tenant,
            CdcTimeout);
        budgetChange.TransactionId.ShouldBe(message.TransactionId, "the budget and its reservation change in one transaction");

        await using var db = app.Infra.NewDbContext();
        var audit = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
            db.Set<AuditEntry>().Where(e => e.Table == "budget_reservations" && e.PrimaryKey.EndsWith(id.ToString())), Ct);
        audit.TransactionId.ShouldBe(message.TransactionId, "the interceptor row and the change share a transaction");
        audit.Operation.ShouldBe(AuditOperation.Insert);
        audit.TenantId.ShouldBe(tenant);
        audit.UserId.ShouldBe("tom");
        audit.PrimaryKey.ShouldStartWith(budget.Id.ToString());
    }
}
