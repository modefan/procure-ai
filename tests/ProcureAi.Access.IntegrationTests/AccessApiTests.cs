using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>The Access API over HTTP: real Program, Postgres (demo-seeded by <c>--migrate</c>), RabbitMQ and Redis.</summary>
[Collection(AccessInfraCollection.Name)]
public sealed class AccessApiTests(AccessInfraFixture infra) : IAsyncLifetime
{
    private const string Base = "/api/v1/access";
    private const string Acme = "acme";
    private const string Globex = "globex";
    private const string Demo = "demo";

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

    private static HttpRequestMessage Request(HttpMethod method, string path, string token, string? tenantHeader = null, string? ifMatch = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenantHeader is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenantHeader);
        }

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

    private static string ServiceToken() => TestAuth.CreateServiceToken(AccessApiFactory.InternalClientId);

    private HttpRequestMessage Internal(string path, string tenant) => Request(HttpMethod.Get, Base + path, ServiceToken(), tenant);

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    [Fact]
    public async Task Effective_access_for_maria_lists_requests_approve_and_IT_200()
    {
        var response = await _client.SendAsync(
            Internal($"/internal/effective?userId={DemoUsers.Maria}&roles=manager", Acme), Ct);

        var access = await ReadAsync<EffectiveAccessDto>(response);

        access.Permissions.ShouldContain("requests.approve");
        access.Permissions.ShouldNotContain("invoices.release-payment");
        access.CostCenterCodes.ShouldBe(["IT-200", "MK-110"]);
        access.ApprovalLimit.ShouldBe(200_000m);
    }

    [Fact]
    public async Task Effective_access_takes_roles_from_the_caller_not_from_the_stored_user()
    {
        // Ruling 5: Maria is stored as a manager, but the token says requester, and the token is authoritative.
        var response = await _client.SendAsync(
            Internal($"/internal/effective?userId={DemoUsers.Maria}&roles=requester&roles=ghost", Acme), Ct);

        var access = await ReadAsync<EffectiveAccessDto>(response);

        access.Permissions.ShouldNotContain("requests.approve");
        access.Permissions.ShouldContain("requests.create");
    }

    [Fact]
    public async Task Approvers_for_IT_200_resolve_manager_head_and_finance()
    {
        var response = await _client.SendAsync(Internal("/internal/cost-centers/IT-200/approvers", Acme), Ct);

        var approvers = await ReadAsync<CostCenterApproversDto>(response);

        approvers.CostCenterCode.ShouldBe("IT-200");
        approvers.DepartmentCode.ShouldBe("IT");
        approvers.Manager.ShouldNotBeNull().UserId.ShouldBe(DemoUsers.Maria);
        approvers.Manager.ApprovalLimit.ShouldBe(200_000m);
        approvers.Manager.CanApprove.ShouldBeTrue();
        approvers.DepartmentHead.ShouldNotBeNull().UserId.ShouldBe(DemoUsers.Dept);
        approvers.DepartmentHead.ApprovalLimit.ShouldBe(500_000m);
        var finance = approvers.Finance.ShouldHaveSingleItem();
        finance.UserId.ShouldBe(DemoUsers.Thandi);
        finance.ApprovalLimit.ShouldBe(2_000_000m);
        finance.CanApprove.ShouldBeTrue();
    }

    [Fact]
    public async Task Internal_endpoint_refuses_user_token_403()
    {
        var response = await _client.SendAsync(
            Request(HttpMethod.Get, $"{Base}/internal/effective?userId={DemoUsers.Maria}&roles=manager", AdminToken(Acme)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Internal_endpoint_without_a_tenant_is_refused()
    {
        var service = await _client.SendAsync(
            Request(HttpMethod.Get, $"{Base}/internal/cost-centers/IT-200/approvers", ServiceToken()), Ct);
        var apiKey = new HttpRequestMessage(HttpMethod.Get, $"{Base}/internal/cost-centers/IT-200/approvers");
        apiKey.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        var key = await _client.SendAsync(apiKey, Ct);

        service.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a service call names its tenant in X-Tenant-Id");
        key.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the admin API key is told to send the header");
    }

    [Fact]
    public async Task Internal_user_lookup_gives_the_display_name_and_404_outside_the_tenant()
    {
        var maria = await _client.SendAsync(Internal($"/internal/users/{DemoUsers.Maria}", Acme), Ct);
        var mariaAsGlobex = await _client.SendAsync(Internal($"/internal/users/{DemoUsers.Maria}", Globex), Ct);
        var unknown = await _client.SendAsync(Internal("/internal/users/nobody", Acme), Ct);
        var asUser = await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/internal/users/{DemoUsers.Maria}", AdminToken(Acme)), Ct);

        var name = await ReadAsync<UserNameDto>(maria);
        name.UserId.ShouldBe(DemoUsers.Maria);
        name.DisplayName.ShouldBe("Maria Manager");
        mariaAsGlobex.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a user of another tenant looks like no user");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        asUser.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "internal lookups are for services only");
    }

    [Fact]
    public async Task Other_tenant_cost_center_is_404()
    {
        var acmeCostCenterAsGlobex = await _client.SendAsync(Internal("/internal/cost-centers/IT-200/approvers", Globex), Ct);
        var globexCostCenterAsAcme = await _client.SendAsync(Internal("/internal/cost-centers/GX-100/approvers", Acme), Ct);
        var own = await _client.SendAsync(Internal("/internal/cost-centers/GX-100/approvers", Globex), Ct);

        acmeCostCenterAsGlobex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        globexCostCenterAsAcme.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var approvers = await ReadAsync<CostCenterApproversDto>(own);
        approvers.Manager.ShouldNotBeNull().UserId.ShouldBe(DemoUsers.Ben);
        approvers.DepartmentHead.ShouldBeNull("the OPS department has no head");
        approvers.Finance.ShouldBeEmpty();
    }

    [Fact]
    public async Task Setting_matrix_publishes_AccessChanged_through_outbox()
    {
        var tenant = await infra.CreateTenantAsync();
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync(Exchanges.Access);
        var matrix = await ReadAsync<MatrixDto>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/matrix", AdminToken(tenant)), Ct));

        var put = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/matrix/roles/requester", AdminToken(tenant), ifMatch: ETag(matrix.Version),
                body: new { permissions = new[] { "requests.read", "requests.create" } }), Ct);

        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
        var message = await probe.WaitForMessageAsync(
            queue, m => m.GetProperty("tenantId").GetString() == tenant && m.GetProperty("kind").GetString() == "Matrix", TimeSpan.FromSeconds(20));
        message.GetProperty("tenantId").GetString().ShouldBe(tenant);
        message.GetProperty("kind").GetString().ShouldBe("Matrix");
    }

    [Fact]
    public async Task Matrix_read_right_after_a_change_is_fresh_and_the_version_moves()
    {
        var tenant = await infra.CreateTenantAsync();
        var before = await ReadAsync<MatrixDto>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/matrix", AdminToken(tenant)), Ct));
        before.Roles.Single(r => r.Role == "manager").Permissions.ShouldContain("requests.approve");

        var put = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/matrix/roles/manager", AdminToken(tenant), ifMatch: ETag(before.Version),
                body: new { permissions = new[] { "requests.read", "requests.create" } }), Ct);
        var updated = await ReadAsync<MatrixDto>(put);
        var after = await ReadAsync<MatrixDto>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/matrix", AdminToken(tenant)), Ct));

        after.Roles.Single(r => r.Role == "manager").Permissions.ShouldBe(["requests.create", "requests.read"], ignoreOrder: true);
        after.Version.ShouldNotBe(before.Version);
        updated.Version.ShouldBe(after.Version);
        put.Headers.ETag!.Tag.ShouldBe(ETag(after.Version));
    }

    [Fact]
    public async Task Matrix_change_with_a_stale_version_is_412_and_without_If_Match_428()
    {
        var matrix = await ReadAsync<MatrixDto>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/matrix", AdminToken(Demo)), Ct));
        var body = new { permissions = new[] { "requests.read" } };

        var stale = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/matrix/roles/requester", AdminToken(Demo), ifMatch: ETag(matrix.Version + 1000), body: body), Ct);
        var missing = await _client.SendAsync(Request(HttpMethod.Put, $"{Base}/matrix/roles/requester", AdminToken(Demo), body: body), Ct);

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        missing.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
    }

    [Fact]
    public async Task Matrix_change_that_would_lock_admins_out_is_422()
    {
        var matrix = await ReadAsync<MatrixDto>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/matrix", AdminToken(Demo)), Ct));

        var response = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/matrix/roles/admin", AdminToken(Demo), ifMatch: ETag(matrix.Version),
                body: new { permissions = new[] { "admin.users" } }), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("lock admins out");
    }

    [Fact]
    public async Task Matrix_endpoints_need_admin_authorization_and_a_requester_is_403()
    {
        var requester = TestAuth.CreateToken(DemoUsers.Tom, Acme, "requester");

        var read = await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/matrix", requester), Ct);
        var users = await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/users", requester), Ct);

        read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        users.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_matrix_change_takes_effect_for_the_very_next_request()
    {
        var tenant = await infra.CreateTenantAsync();
        // Access's own permission provider reads its database: dropping admin.users from a custom role is enforced at once.
        var matrix = await ReadAsync<MatrixDto>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/matrix", AdminToken(tenant)), Ct));
        var auditor = TestAuth.CreateToken(DemoUsers.Admin, tenant, "auditor");
        (await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/users", auditor), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var grant = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/matrix/roles/auditor", AdminToken(tenant), ifMatch: ETag(matrix.Version),
                body: new { permissions = new[] { "admin.users" } }), Ct);
        grant.StatusCode.ShouldBe(HttpStatusCode.OK, await grant.Content.ReadAsStringAsync(Ct));

        (await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/users", auditor), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Approval_limit_change_is_visible_in_the_next_read_and_publishes_a_user_event()
    {
        var tenant = await infra.CreateTenantAsync();
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync(Exchanges.Access);
        var users = await ReadAsync<List<UserDto>>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/users", AdminToken(tenant)), Ct));
        var maria = users.ShouldHaveSingleItem();
        maria.UserId.ShouldBe(DemoUsers.Maria);
        maria.ApprovalLimit.ShouldBe(200_000m);

        var put = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/users/{DemoUsers.Maria}/approval-limit", AdminToken(tenant), ifMatch: ETag(maria.Version),
                body: new { amount = 150_000m }), Ct);
        var updated = await ReadAsync<UserDto>(put);
        var after = (await ReadAsync<List<UserDto>>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/users", AdminToken(tenant)), Ct))).Single();

        updated.ApprovalLimit.ShouldBe(150_000m);
        after.ApprovalLimit.ShouldBe(150_000m, "the read right after the write must not be served from the 1 s cache");
        after.Version.ShouldNotBe(maria.Version);
        var message = await probe.WaitForMessageAsync(
            queue, m => m.GetProperty("tenantId").GetString() == tenant && m.GetProperty("kind").GetString() == "User"
                         && m.GetProperty("userId").GetString() == DemoUsers.Maria, TimeSpan.FromSeconds(20));
        message.GetProperty("kind").GetString().ShouldBe("User");
        message.GetProperty("userId").GetString().ShouldBe(DemoUsers.Maria);
        message.GetProperty("tenantId").GetString().ShouldBe(tenant);
    }

    [Fact]
    public async Task Approval_limit_below_zero_is_a_400_and_an_unknown_user_a_404()
    {
        var users = await ReadAsync<List<UserDto>>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/users", AdminToken(Globex)), Ct));
        var ben = users.Single();

        var negative = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/users/{DemoUsers.Ben}/approval-limit", AdminToken(Globex), ifMatch: ETag(ben.Version), body: new { amount = -1m }), Ct);
        var unknown = await _client.SendAsync(
            Request(HttpMethod.Put, $"{Base}/users/{DemoUsers.Tom}/approval-limit", AdminToken(Globex), ifMatch: ETag(1), body: new { amount = 1m }), Ct);

        negative.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound, "tom belongs to acme, not globex");
    }

    [Fact]
    public async Task Users_of_another_tenant_are_not_listed()
    {
        var acme = await ReadAsync<List<UserDto>>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/users", AdminToken(Acme)), Ct));
        var demo = await ReadAsync<List<UserDto>>(await _client.SendAsync(Request(HttpMethod.Get, $"{Base}/users", AdminToken(Demo)), Ct));

        acme.Select(u => u.UserId).ShouldBe(
            [DemoUsers.Tom, DemoUsers.Maria, DemoUsers.Dept, DemoUsers.Thandi, DemoUsers.Admin], ignoreOrder: true);
        demo.ShouldBeEmpty();
    }

    [Fact]
    public async Task Admin_api_key_with_tenant_header_reads_the_matrix()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/matrix");
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", Acme);

        var matrix = await ReadAsync<MatrixDto>(await _client.SendAsync(request, Ct));

        matrix.TenantId.ShouldBe(Acme);
        matrix.Roles.Select(r => r.Role).ShouldBe(["admin", "department-head", "finance", "manager", "requester"]);
        matrix.AvailablePermissions.Count.ShouldBe(Permissions.All.Count);
    }

    private static string ETag(uint version) => $"\"{version}\"";

}
