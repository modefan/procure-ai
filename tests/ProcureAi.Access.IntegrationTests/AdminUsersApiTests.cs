using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Domain;
using ProcureAi.Access.Infrastructure;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>Plan 06 Task 1: the cost center list, the cost center assignment of a user and the access explanation.</summary>
[Collection(AccessInfraCollection.Name)]
public sealed class AdminUsersApiTests(AccessInfraFixture infra) : IAsyncLifetime
{
    private const string Base = "/api/v1/access";
    private const string Acme = "acme";
    private const string Globex = "globex";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private AccessApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new AccessApiFactory(infra);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token, string? ifMatch = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static string AdminToken(string tenant) => TestAuth.CreateToken(DemoUsers.Admin, tenant, "admin");

    private static string ETag(uint version) => $"\"{version}\"";

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    private async Task<string> TenantWithCostCentersAsync()
    {
        var tenant = await infra.CreateTenantAsync();
        await using var db = new AccessDbContext(new DbContextOptionsBuilder<AccessDbContext>().UseNpgsql(infra.DbConnectionString).Options);
        db.Departments.Add(Department.Create(tenant, "D1", "Dept One", DemoUsers.Maria));
        db.CostCenters.AddRange(
            CostCenter.Create(tenant, "CC-1", "First", "D1", DemoUsers.Maria),
            CostCenter.Create(tenant, "CC-2", "Second", "D1"));
        await db.SaveChangesAsync(Ct);
        return tenant;
    }

    private async Task<UserDto> MariaAsync(string tenant) =>
        (await ReadAsync<List<UserDto>>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/users", AdminToken(tenant)), Ct)))
            .Single(u => u.UserId == DemoUsers.Maria);

    [Fact]
    public async Task Cost_centers_list_has_department_manager_and_head_names()
    {
        var list = await ReadAsync<List<CostCenterAdminDto>>(
            await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/cost-centers", AdminToken(Acme)), Ct));

        var it = list.Single(c => c.Code == "IT-200");
        it.Name.ShouldBe("IT Operations");
        it.DepartmentCode.ShouldBe("IT");
        it.DepartmentName.ShouldBe("Information Technology");
        it.ManagerId.ShouldBe(DemoUsers.Maria);
        it.ManagerName.ShouldBe("Maria Naidoo");
        it.DepartmentHeadId.ShouldBe(DemoUsers.Dept);
        it.DepartmentHeadName.ShouldBe("Dept Head");
    }

    [Fact]
    public async Task Cost_centers_of_other_tenant_are_not_listed()
    {
        var acme = await ReadAsync<List<CostCenterAdminDto>>(
            await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/cost-centers", AdminToken(Acme)), Ct));
        var globex = await ReadAsync<List<CostCenterAdminDto>>(
            await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/cost-centers", AdminToken(Globex)), Ct));

        acme.Select(c => c.Code).ShouldBe(["IT-200", "MK-110"], ignoreOrder: true);
        globex.Select(c => c.Code).ShouldBe(["GX-100"]);
        globex.Single().DepartmentHeadName.ShouldBeNull("the OPS department has no head");
    }

    [Fact]
    public async Task Set_cost_centers_replaces_and_publishes_access_changed()
    {
        var tenant = await TenantWithCostCentersAsync();
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync(Exchanges.Access);
        var maria = await MariaAsync(tenant);

        var put = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/users/{DemoUsers.Maria}/cost-centers", AdminToken(tenant), ETag(maria.Version),
                new { codes = new[] { "CC-2", "CC-1", "CC-2" } }), Ct);

        var updated = await ReadAsync<UserDto>(put);
        updated.CostCenterCodes.ShouldBe(["CC-2", "CC-1"]);
        updated.Version.ShouldNotBe(maria.Version);
        (await MariaAsync(tenant)).CostCenterCodes.ShouldBe(["CC-2", "CC-1"], "the read right after the write must not be served from the cache");
        var message = await probe.WaitForMessageAsync(
            queue, m => m.GetProperty("tenantId").GetString() == tenant && m.GetProperty("kind").GetString() == "User"
                         && m.GetProperty("userId").GetString() == DemoUsers.Maria, TimeSpan.FromSeconds(20));
        message.GetProperty("userId").GetString().ShouldBe(DemoUsers.Maria);

        var cleared = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/users/{DemoUsers.Maria}/cost-centers", AdminToken(tenant), ETag(updated.Version),
                new { codes = Array.Empty<string>() }), Ct);
        (await ReadAsync<UserDto>(cleared)).CostCenterCodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Set_cost_centers_unknown_code_is_422()
    {
        var tenant = await TenantWithCostCentersAsync();
        var maria = await MariaAsync(tenant);

        var put = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/users/{DemoUsers.Maria}/cost-centers", AdminToken(tenant), ETag(maria.Version),
                new { codes = new[] { "CC-1", "NOPE-9" } }), Ct);
        var otherTenantCode = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/users/{DemoUsers.Maria}/cost-centers", AdminToken(tenant), ETag(maria.Version),
                new { codes = new[] { "IT-200" } }), Ct);

        put.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await put.Content.ReadAsStringAsync(Ct)).ShouldContain("NOPE-9");
        otherTenantCode.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "a cost center of another tenant does not exist here");
        (await MariaAsync(tenant)).CostCenterCodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Set_cost_centers_stale_version_is_412()
    {
        var tenant = await TenantWithCostCentersAsync();
        var maria = await MariaAsync(tenant);

        var stale = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/users/{DemoUsers.Maria}/cost-centers", AdminToken(tenant), ETag(maria.Version + 1000),
                new { codes = new[] { "CC-1" } }), Ct);
        var missing = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/users/{DemoUsers.Maria}/cost-centers", AdminToken(tenant), body: new { codes = new[] { "CC-1" } }), Ct);

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        missing.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
    }

    [Fact]
    public async Task Set_cost_centers_without_admin_users_is_403()
    {
        var requester = TestAuth.CreateToken(DemoUsers.Tom, Acme, "requester");

        var list = await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/cost-centers", requester), Ct);
        var put = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/users/{DemoUsers.Tom}/cost-centers", requester, ETag(1), new { codes = new[] { "IT-200" } }), Ct);

        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        put.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Explain_lists_roles_granting_each_permission()
    {
        var service = TestAuth.CreateServiceToken(AccessApiFactory.InternalClientId);
        var request = Request(HttpMethod.Get, $"{Base}/internal/explain?userId={DemoUsers.Dept}", service);
        request.Headers.Add("X-Tenant-Id", Acme);

        var explanation = await ReadAsync<AccessExplanationDto>(await _client.SendAsync(request, Ct));

        explanation.UserId.ShouldBe(DemoUsers.Dept);
        explanation.DisplayName.ShouldBe("Dept Head");
        explanation.TenantId.ShouldBe(Acme);
        explanation.Roles.ShouldBe(["department-head"]);
        explanation.PermissionSources["requests.approve"].ShouldBe(["department-head"]);
        explanation.PermissionSources.ShouldNotContainKey("invoices.release-payment");
        explanation.DepartmentCodes.ShouldBe(["IT", "MK"], ignoreOrder: true);
        explanation.CostCenterCodes.ShouldBe(["IT-200", "MK-110"], ignoreOrder: true);
        explanation.ApprovalLimit.ShouldBe(500_000m);
        explanation.IsFinanceApprover.ShouldBeFalse();
    }

    [Fact]
    public async Task Explain_unknown_user_is_404()
    {
        var service = TestAuth.CreateServiceToken(AccessApiFactory.InternalClientId);
        var request = Request(HttpMethod.Get, $"{Base}/internal/explain?userId={DemoUsers.Ben}", service);
        request.Headers.Add("X-Tenant-Id", Acme);

        (await _client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var userToken = Request(HttpMethod.Get, $"{Base}/internal/explain?userId={DemoUsers.Dept}", AdminToken(Acme));
        (await _client.SendAsync(userToken, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
