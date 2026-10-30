using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.Access.IntegrationTests;

/// <summary><c>GET access/me</c>: who the caller is, for forms and the dashboard.</summary>
[Collection(AccessInfraCollection.Name)]
public sealed class MeTests(AccessInfraFixture infra) : IAsyncLifetime
{
    private const string Me = "/api/v1/access/me";

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

    private async Task<MeDto> MeAsync(string user, string tenant, string role)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Me);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateToken(user, tenant, role));
        using var response = await _client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<MeDto>(Json, Ct))!;
    }

    [Fact]
    public async Task Me_has_tenant_name()
    {
        (await MeAsync(DemoUsers.Maria, "acme", "manager")).TenantName.ShouldBe("Acme Corp");
        (await MeAsync(DemoUsers.Ben, "globex", "manager")).TenantName.ShouldBe("Globex");
        (await MeAsync(DemoUsers.DemoApprover, "demo", "manager")).TenantName.ShouldBe("Demo company");
    }

    [Fact]
    public async Task Me_returns_cost_centers_and_limit()
    {
        var maria = await MeAsync(DemoUsers.Maria, "acme", "manager");
        var thandi = await MeAsync(DemoUsers.Thandi, "acme", "finance");
        var tom = await MeAsync(DemoUsers.Tom, "acme", "requester");

        maria.UserId.ShouldBe(DemoUsers.Maria);
        maria.Name.ShouldBe("Maria Manager");
        maria.Roles.ShouldBe(["manager"]);
        maria.CostCenters.ShouldContain(c => c.Code == "IT-200" && c.Name == "IT Operations" && c.DepartmentCode == "IT");
        maria.ApprovalLimit.ShouldBe(200_000m);
        thandi.CostCenters.Select(c => c.Code).ShouldBe(["IT-200", "MK-110"], "finance acts for every cost center of the tenant");
        thandi.ApprovalLimit.ShouldBe(2_000_000m);
        tom.CostCenters.Select(c => c.Code).ShouldBe(["IT-200"]);
        tom.ApprovalLimit.ShouldBeNull("a zero limit means the user cannot approve anything");
    }

    [Fact]
    public async Task Me_of_other_tenant_user_is_own_tenant_only()
    {
        var ben = await MeAsync(DemoUsers.Ben, "globex", "manager");
        // Maria's token claims the globex tenant: Globex has no such user, so nothing of Acme shows.
        var impostor = await MeAsync(DemoUsers.Maria, "globex", "manager");

        ben.CostCenters.Select(c => c.Code).ShouldBe(["GX-100"]);
        ben.Name.ShouldBe("Ben Manager");
        impostor.CostCenters.ShouldBeEmpty();
        impostor.ApprovalLimit.ShouldBeNull();
        impostor.Name.ShouldBe(DemoUsers.Maria, "an unknown user is shown by ID");
    }

    [Fact]
    public async Task Me_needs_a_login()
    {
        using var response = await _client.GetAsync(Me, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
