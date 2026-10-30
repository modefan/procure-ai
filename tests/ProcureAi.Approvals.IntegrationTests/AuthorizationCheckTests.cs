using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ProcureAi.Approvals.Application.AuthorizationCheck;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;
using Shouldly;
using static ProcureAi.Approvals.IntegrationTests.ApprovalsApp;

namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>
/// The admin's authorization check (plan 06 ruling 6): why a user may or may not view or approve a request, on the real API with
/// the stub Access answering the explanation of the user and the organisation of the cost center.
/// </summary>
[Collection(ApprovalsCollection.Name)]
public sealed class AuthorizationCheckTests(ApprovalsApp app)
{
    private const string Base = "/api/v1/approvals/authorization-check";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApprovalsInstance Main => app.Main;

    private static string Rand(decimal amount) => Money.Zar(amount).ToDisplayString();

    /// <summary>Acme for the tenant: the IT-200 organisation, the tenant's name and the stored users the check explains.</summary>
    private string Acme()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        app.Access.SetTenantName(tenant, "Acme Corp");
        app.Access.SetExplainedUser(tenant, DemoUsers.Maria, "Maria Naidoo", ["manager"], 200_000m, [CostCenter]);
        app.Access.SetExplainedUser(tenant, DemoUsers.Tom, "Tom Botha", ["requester"], 0m, [CostCenter]);
        return tenant;
    }

    [Fact]
    public async Task Check_maria_on_own_request_is_denied_by_separation_of_duties()
    {
        var tenant = Acme();
        var (id, steps) = await OpenAsync(tenant, DemoUsers.Maria, 37_260m);
        // Maria is the manager of IT-200 but raised the request: routing moved step 1 up to the department head.
        steps[0].Level.ShouldBe(ApprovalLevel.DepartmentHead);

        var check = await CheckAsync(tenant, DemoUsers.Maria, id, "approve");

        check.Allowed.ShouldBeFalse();
        check.Action.ShouldBe("approve");
        check.UserName.ShouldBe("Maria Naidoo");
        check.RequestNumber.ShouldBe(steps[0].RequestNumber);
        check.Steps.Select(s => (s.Code, s.Passed, s.Text)).ShouldBe(
        [
            ("permission", true, "Role Manager has requests.approve"),
            ("tenant", true, "Same tenant (Acme Corp)"),
            ("open-step", true, "Step 1 (Department head) is open"),
            ("cost-center", true, "Cost center IT-200 is assigned to Maria Naidoo"),
            ("limit", true, $"{Rand(37_260m)} is within the {Rand(200_000m)} limit"),
            ("not-requester", false, "Separation of duties: Maria Naidoo is the requester"),
            ("current-approver", false, "Maria Naidoo is not an approver of step 1"),
        ]);
        check.Steps.Where(s => !s.Gating).Select(s => s.Code).ShouldBe(["cost-center", "limit"], "routing eligibility explains, it does not gate");
    }

    [Fact]
    public async Task Check_maria_on_tom_request_is_allowed()
    {
        var tenant = Acme();
        var (id, _) = await OpenAsync(tenant, DemoUsers.Tom, 37_260m);

        var check = await CheckAsync(tenant, DemoUsers.Maria, id, "approve");
        var view = await CheckAsync(tenant, DemoUsers.Maria, id, "view");

        check.Allowed.ShouldBeTrue();
        check.Steps.Select(s => (s.Code, s.Passed, s.Text)).ShouldBe(
        [
            ("permission", true, "Role Manager has requests.approve"),
            ("tenant", true, "Same tenant (Acme Corp)"),
            ("open-step", true, "Step 1 (Manager) is open"),
            ("cost-center", true, "Cost center IT-200 is assigned to Maria Naidoo"),
            ("limit", true, $"{Rand(37_260m)} is within the {Rand(200_000m)} limit"),
            ("not-requester", true, "Separation of duties: Maria Naidoo is not the requester"),
            ("current-approver", true, "Maria Naidoo is an approver of step 1"),
        ]);
        check.Steps.Where(s => !s.Gating).Select(s => s.Code).ShouldBe(["cost-center", "limit"]);
        view.Allowed.ShouldBeTrue();
        view.Steps.Select(s => s.Code).ShouldBe(["permission", "tenant"]);
        view.Steps[0].Text.ShouldBe("Role Manager has requests.read");
    }

    [Fact]
    public async Task Check_after_access_lowered_the_limit_is_allowed_as_the_approve_action_is()
    {
        var tenant = Acme();
        var (id, steps) = await OpenAsync(tenant, DemoUsers.Tom, 37_260m);
        // Routed to Maria (R 200 000 in the organisation); her stored limit is lowered afterwards.
        steps[0].ApproverIds.ShouldBe([DemoUsers.Maria]);
        app.Access.SetExplainedUser(tenant, DemoUsers.Maria, "Maria Naidoo", ["manager"], 20_000m, [CostCenter]);

        var check = await CheckAsync(tenant, DemoUsers.Maria, id, "approve");
        using var client = Main.CreateClient();
        var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/approvals/{steps[0].Id}/approve");
        User(tenant, DemoUsers.Maria, "manager")(approve);
        approve.Headers.TryAddWithoutValidation("If-Match", $"\"{steps[0].Version}\"");
        approve.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var approved = await client.SendAsync(approve, Ct);

        // The approve action does not re-check routing eligibility, so neither does the outcome of the check.
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        check.Allowed.ShouldBeTrue();
        var limit = check.Steps.Single(s => s.Code == "limit");
        (limit.Passed, limit.Gating, limit.Text).ShouldBe(
            (false, false, $"Routing today would not pick Maria Naidoo: {Rand(37_260m)} is above the {Rand(20_000m)} limit"));
        check.Steps.Single(s => s.Code == "current-approver").Passed.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_when_routing_skipped_her_for_the_limit_is_denied_by_current_approver()
    {
        var tenant = Acme();
        app.Access.SetApprovers(tenant, new CostCenterApprovers(
            CostCenter, "IT",
            new Approver(DemoUsers.Maria, "Maria", 20_000m, true),
            new Approver(DemoUsers.Dept, "Dept", 500_000m, true),
            [new Approver(DemoUsers.Thandi, "Thandi", 2_000_000m, true)]));
        app.Access.SetExplainedUser(tenant, DemoUsers.Maria, "Maria Naidoo", ["manager"], 20_000m, [CostCenter]);
        var (id, steps) = await OpenAsync(tenant, DemoUsers.Tom, 37_260m);
        steps[0].ApproverIds.ShouldBe([DemoUsers.Dept]);

        var check = await CheckAsync(tenant, DemoUsers.Maria, id, "approve");

        check.Allowed.ShouldBeFalse();
        var current = check.Steps.Single(s => s.Code == "current-approver");
        (current.Passed, current.Gating).ShouldBe((false, true));
        check.Steps.Where(s => s.Gating && !s.Passed).Select(s => s.Code).ShouldBe(["current-approver"], "denied by the step the action enforces");
        var limit = check.Steps.Single(s => s.Code == "limit");
        (limit.Passed, limit.Gating).ShouldBe((false, false));
    }

    [Fact]
    public async Task Check_view_without_requests_read_is_denied_with_role_text()
    {
        var tenant = Acme();
        app.Access.SetMatrix(tenant, new Dictionary<string, string[]>
        {
            ["requester"] = [Permissions.RequestsCreate],
            ["admin"] = [Permissions.AdminAuthorization],
        });
        var (id, _) = await OpenAsync(tenant, DemoUsers.Tom, 5_000m);

        var check = await CheckAsync(tenant, DemoUsers.Tom, id, "view");

        check.Allowed.ShouldBeFalse();
        check.UserName.ShouldBe("Tom Botha");
        check.Steps.Select(s => (s.Code, s.Passed, s.Text)).ShouldBe(
        [
            ("permission", false, "Role Requester does not have requests.read"),
            ("tenant", true, "Same tenant (Acme Corp)"),
        ]);
    }

    [Fact]
    public async Task Check_for_other_tenant_request_is_404()
    {
        var tenant = Acme();
        var globex = Acme();
        var (acmeRequest, _) = await OpenAsync(tenant, DemoUsers.Tom, 5_000m);
        var (globexRequest, _) = await OpenAsync(globex, DemoUsers.Tom, 5_000m);
        app.Access.SetExplainedUser(globex, DemoUsers.Ben, "Ben Globex", ["manager"], 200_000m);
        using var client = Main.CreateClient();

        var otherRequest = await client.SendAsync(Get(Query(DemoUsers.Maria, globexRequest, "approve"), Admin(tenant)), Ct);
        var otherUser = await client.SendAsync(Get(Query(DemoUsers.Ben, acmeRequest, "view"), Admin(tenant)), Ct);
        var unknownRequest = await client.SendAsync(Get(Query(DemoUsers.Maria, Guid.NewGuid(), "view"), Admin(tenant)), Ct);
        var withKey = await client.SendAsync(Get(Query(DemoUsers.Maria, globexRequest, "view"), AdminKey(tenant)), Ct);

        otherRequest.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a request of another tenant does not exist here");
        otherUser.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a user of another tenant does not exist here");
        unknownRequest.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        withKey.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the admin key is scoped by X-Tenant-Id too");
    }

    [Fact]
    public async Task Check_without_admin_authorization_is_403()
    {
        var tenant = Acme();
        var (id, _) = await OpenAsync(tenant, DemoUsers.Tom, 5_000m);
        using var client = Main.CreateClient();

        var asManager = await client.SendAsync(Get(Query(DemoUsers.Maria, id, "approve"), User(tenant, DemoUsers.Maria, "manager")), Ct);
        var badAction = await client.SendAsync(Get(Query(DemoUsers.Maria, id, "pay"), Admin(tenant)), Ct);
        var withKey = await client.SendAsync(Get(Query(DemoUsers.Maria, id, "approve"), AdminKey(tenant)), Ct);

        asManager.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        badAction.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "only view and approve are explained");
        withKey.StatusCode.ShouldBe(HttpStatusCode.OK, "the admin API key has every permission");
    }

    [Fact]
    public async Task Check_on_completed_request_reports_no_open_step()
    {
        var tenant = Acme();
        var (id, steps) = await OpenAsync(tenant, DemoUsers.Tom, 5_000m);
        await Main.InvokeAsync(new ApproveStep(id, steps[0].Id, steps[0].Version), tenant, DemoUsers.Maria);
        (await Main.SagaAsync(id))!.State.ShouldNotBe(ApprovalState.AwaitingApproval);

        var check = await CheckAsync(tenant, DemoUsers.Maria, id, "approve");

        check.Allowed.ShouldBeFalse();
        check.Steps.Select(s => (s.Code, s.Passed, s.Text)).ShouldBe(
        [
            ("permission", true, "Role Manager has requests.approve"),
            ("tenant", true, "Same tenant (Acme Corp)"),
            ("open-step", false, "The request has no open approval step"),
        ]);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    private async Task<(Guid Id, List<ApprovalStep> Steps)> OpenAsync(string tenant, string requester, decimal total)
    {
        var submitted = Submitted(tenant, requester, total);
        await Main.InvokeAsync(submitted, tenant, requester);
        await Main.InvokeAsync(Reserved(submitted), tenant, requester);
        return (submitted.RequestId, await Main.StepsAsync(submitted.RequestId));
    }

    private async Task<AuthorizationCheckDto> CheckAsync(string tenant, string userId, Guid requestId, string action)
    {
        using var client = Main.CreateClient();
        var response = await client.SendAsync(Get(Query(userId, requestId, action), Admin(tenant)), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<AuthorizationCheckDto>(Ct))!;
    }

    private static string Query(string userId, Guid requestId, string action) =>
        $"?userId={Uri.EscapeDataString(userId)}&requestId={requestId}&action={action}";

    private static Action<HttpRequestMessage> User(string tenant, string user, string role) =>
        r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateToken(user, tenant, role));

    private static Action<HttpRequestMessage> Admin(string tenant) => User(tenant, DemoUsers.Admin, "admin");

    private static Action<HttpRequestMessage> AdminKey(string tenant) =>
        r =>
        {
            r.Headers.Add("X-Api-Key", TestAuth.ApiKey);
            r.Headers.Add("X-Tenant-Id", tenant);
        };

    private static HttpRequestMessage Get(string query, Action<HttpRequestMessage> caller)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Base + query);
        caller(request);
        return request;
    }
}
