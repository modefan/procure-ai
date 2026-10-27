using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Domain;
using ProcureAi.Access.Infrastructure;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>
/// Self-registration: a user who signed up in Keycloak joins the demo company (organization and role in Keycloak, tenant user
/// in Access). The "demo" organization is a fresh one per test, so no test touches the seeded demo tenant or another's users.
/// </summary>
[Collection(AccessInfraCollection.Name)]
public sealed class RegistrationTests(AccessInfraFixture infra, LazyKeycloak lazyKeycloak) : IAsyncLifetime
{
    private const string Url = "/api/v1/access/internal/registrations";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private KeycloakFixture _keycloak = null!;
    private AccessApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _tenant = "";
    private string _organizationId = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _keycloak = await lazyKeycloak.GetAsync();
        _tenant = $"d{Guid.NewGuid():N}"[..12];
        _organizationId = await _keycloak.CreateOrganizationAsync(_tenant);
        await infra.CreateTenantAsync(_tenant);
        await using (var db = new AccessDbContext(new DbContextOptionsBuilder<AccessDbContext>().UseNpgsql(infra.DbConnectionString).Options))
        {
            // The demo company's sandbox cost center, as the demo seed creates it for the real "demo" tenant.
            db.CostCenters.Add(CostCenter.Create(_tenant, AccessApiFactory.SandboxCostCenter, "Sandbox", "SANDBOX"));
            await db.SaveChangesAsync();
        }

        _factory = new AccessApiFactory(infra, _keycloak, demoOrganization: _tenant);
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<HttpResponseMessage> RegisterAsync(
        string userId, string email, string name, string? tenant = null, string? key = null, string? onBehalfOf = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(new { userId, email, displayName = name }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateServiceToken(AccessApiFactory.InternalClientId));
        request.Headers.Add("X-Tenant-Id", tenant ?? _tenant);
        request.Headers.Add("X-Calling-Service", "gateway");
        // "" means "the registered user" (what the gateway sends); null sends no header at all.
        if (onBehalfOf is not null)
        {
            request.Headers.Add("X-On-Behalf-Of-User", onBehalfOf == "" ? userId : onBehalfOf);
        }

        request.Headers.Add("Idempotency-Key", key ?? $"register-{Guid.NewGuid():N}");
        return await _client.SendAsync(request, Ct);
    }

    private async Task<List<UserDto>> UsersAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/access/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateToken("admin-of-" + _tenant, _tenant, "admin"));
        using var response = await _client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<List<UserDto>>(Json, Ct))!;
    }

    private static string NewEmail() => $"new-{Guid.NewGuid():N}@registered.test";

    [Fact]
    public async Task Registration_adds_user_to_demo_org_with_requester_role()
    {
        var email = NewEmail();
        var userId = await _keycloak.CreateUserAsync(email);
        (await _keycloak.IsOrganizationMemberAsync(_organizationId, userId)).ShouldBeFalse();
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync(Exchanges.Access);

        // The body's email and name are ignored: they come from the identity provider (Keycloak: "New Person").
        using var response = await RegisterAsync(userId, "forged@elsewhere.test", "Thandi Finance");

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        (await _keycloak.IsOrganizationMemberAsync(_organizationId, userId)).ShouldBeTrue();
        (await _keycloak.RealmRolesOfAsync(userId)).ShouldContain("requester");
        var user = (await UsersAsync()).Single(u => u.UserId == userId);
        user.Email.ShouldBe(email);
        user.DisplayName.ShouldBe("New Person");
        user.Roles.ShouldBe(["requester"]);
        user.ApprovalLimit.ShouldBe(0m);
        user.IsFinanceApprover.ShouldBeFalse();
        user.CostCenterCodes.ShouldBe([AccessApiFactory.SandboxCostCenter]);
        var message = await probe.WaitForMessageAsync(
            queue, m => m.GetProperty("tenantId").GetString() == _tenant && m.GetProperty("userId").GetString() == userId,
            TimeSpan.FromSeconds(20));
        message.GetProperty("kind").GetString().ShouldBe("User");
    }

    [Fact]
    public async Task A_service_registering_someone_other_than_the_user_it_acts_for_is_403()
    {
        // Final review I-5: a service (or a leaked internal secret) must not put another person into the demo company.
        var email = NewEmail();
        var victim = await _keycloak.CreateUserAsync(email);
        var caller = await _keycloak.CreateUserAsync(NewEmail());

        using var forgedBody = await RegisterAsync(victim, email, "Victim", onBehalfOf: caller);
        using var noHeader = await RegisterAsync(victim, email, "Victim", onBehalfOf: null);

        forgedBody.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await forgedBody.Content.ReadAsStringAsync(Ct));
        noHeader.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await noHeader.Content.ReadAsStringAsync(Ct));
        (await _keycloak.IsOrganizationMemberAsync(_organizationId, victim)).ShouldBeFalse();
        (await UsersAsync()).ShouldNotContain(u => u.UserId == victim);
    }

    [Fact]
    public async Task A_user_of_another_company_is_refused_409_and_nothing_changes()
    {
        // Review minor 1: a second organization would make the tenant of the user's token undefined, and the realm role
        // "requester" would also apply in their own company.
        var email = NewEmail();
        var userId = await _keycloak.CreateUserAsync(email);
        var other = $"o{Guid.NewGuid():N}"[..12];
        await _keycloak.AddOrganizationMemberAsync(await _keycloak.CreateOrganizationAsync(other), userId);

        using var response = await RegisterAsync(userId, email, "Someone");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await _keycloak.IsOrganizationMemberAsync(_organizationId, userId)).ShouldBeFalse();
        (await _keycloak.RealmRolesOfAsync(userId)).ShouldNotContain("requester");
        (await UsersAsync()).ShouldNotContain(u => u.UserId == userId);
    }

    [Fact]
    public async Task An_email_too_long_to_store_is_422_before_keycloak_is_changed()
    {
        // Review minor 2: the refusal must not leave a half-registered user (in the organization, without an Access row).
        var label = new string('a', 60);
        var email = $"{new string('x', 60)}@{label}.{label}.{label}.test";
        email.Length.ShouldBeGreaterThan(200);
        var userId = await _keycloak.CreateUserAsync(email);

        using var response = await RegisterAsync(userId, email, "Long Email");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        (await _keycloak.IsOrganizationMemberAsync(_organizationId, userId)).ShouldBeFalse();
        (await _keycloak.RealmRolesOfAsync(userId)).ShouldNotContain("requester");
    }

    [Fact]
    public async Task Registration_twice_changes_nothing()
    {
        var email = NewEmail();
        var userId = await _keycloak.CreateUserAsync(email);
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync(Exchanges.Access);
        using var first = await RegisterAsync(userId, email, "New Person");
        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync(Ct));
        await probe.WaitForMessageAsync(queue, m => m.GetProperty("userId").GetString() == userId, TimeSpan.FromSeconds(20));
        var before = (await UsersAsync()).Single(u => u.UserId == userId);

        // A new Idempotency-Key, so the handler itself (not the idempotency replay) decides.
        using var second = await RegisterAsync(userId, email, "Someone Else");

        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(Ct));
        var after = (await UsersAsync()).Single(u => u.UserId == userId);
        // Lists compare by reference in a record, so compare the parts.
        after.Version.ShouldBe(before.Version);
        after.DisplayName.ShouldBe(before.DisplayName);
        after.Email.ShouldBe(before.Email);
        after.Roles.ShouldBe(before.Roles);
        after.ApprovalLimit.ShouldBe(before.ApprovalLimit);
        after.CostCenterCodes.ShouldBe(before.CostCenterCodes);
        await Should.ThrowAsync<TimeoutException>(() => probe.WaitForMessageAsync(
            queue, m => m.GetProperty("userId").GetString() == userId, TimeSpan.FromSeconds(3)));
        (await _keycloak.IsOrganizationMemberAsync(_organizationId, userId)).ShouldBeTrue();
    }

    private async Task RunParallelRegistrationAsync(Func<int, string?> keyFor)
    {
        var email = NewEmail();
        var userId = await _keycloak.CreateUserAsync(email);
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync(Exchanges.Access);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => RegisterAsync(userId, email, "New Person", key: keyFor(i))));

        foreach (var response in responses)
        {
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
            response.Dispose();
        }

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBeGreaterThanOrEqualTo(1);
        (await UsersAsync()).Count(u => u.UserId == userId).ShouldBe(1);
        // Losing racers must not leave an audit row of an insert that never happened.
        await using var audit = new AccessDbContext(new DbContextOptionsBuilder<AccessDbContext>().UseNpgsql(infra.DbConnectionString).Options);
        (await audit.Set<AuditEntry>()
            .Where(a => a.Table == "tenant_users" && a.Operation == AuditOperation.Insert && a.PrimaryKey.Contains(userId))
            .CountAsync(Ct)).ShouldBe(1);
        await probe.WaitForMessageAsync(queue, m => m.GetProperty("userId").GetString() == userId, TimeSpan.FromSeconds(20));
        await Should.ThrowAsync<TimeoutException>(() => probe.WaitForMessageAsync(
            queue, m => m.GetProperty("userId").GetString() == userId, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Parallel_registrations_with_the_same_key_run_once()
    {
        var key = $"register-{Guid.NewGuid():N}";
        await RunParallelRegistrationAsync(_ => key);
    }

    [Fact]
    public async Task Parallel_registrations_with_different_keys_do_not_fail_on_the_duplicate_insert()
    {
        // The idempotency layer is bypassed (a key per call): the handler itself must survive the unique violation.
        await RunParallelRegistrationAsync(_ => null);
    }

    [Fact]
    public async Task Registration_heals_a_user_whose_earlier_attempt_stopped_after_keycloak()
    {
        // Keycloak was changed but Access never stored the user (it failed in between): the retry finishes the job.
        var email = NewEmail();
        var userId = await _keycloak.CreateUserAsync(email);
        using var http = await _keycloak.AdminHttpAsync();
        using var added = await http.PostAsJsonAsync($"/admin/realms/{KeycloakFixture.Realm}/organizations/{_organizationId}/members", userId, Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var response = await RegisterAsync(userId, email, "New Person");

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        (await _keycloak.RealmRolesOfAsync(userId)).ShouldContain("requester");
        (await UsersAsync()).ShouldContain(u => u.UserId == userId);
    }

    [Fact]
    public async Task Registration_of_a_user_unknown_to_keycloak_is_404_and_stores_nothing()
    {
        var userId = Guid.NewGuid().ToString();

        using var response = await RegisterAsync(userId, NewEmail(), "Ghost");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(Ct));
        (await UsersAsync()).ShouldNotContain(u => u.UserId == userId);
    }

    [Fact]
    public async Task Registration_for_a_tenant_other_than_the_demo_company_is_refused()
    {
        var email = NewEmail();
        var userId = await _keycloak.CreateUserAsync(email);
        var other = await infra.CreateTenantAsync();

        using var response = await RegisterAsync(userId, email, "New Person", tenant: other);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        (await _keycloak.IsOrganizationMemberAsync(_organizationId, userId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_normal_user_token_cannot_register_anyone()
    {
        var email = NewEmail();
        var userId = await _keycloak.CreateUserAsync(email);
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(new { userId, email, displayName = "X" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateToken("someone", _tenant, "admin"));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        using var response = await _client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _keycloak.IsOrganizationMemberAsync(_organizationId, userId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Registration_needs_an_idempotency_key()
    {
        var email = NewEmail();
        var userId = await _keycloak.CreateUserAsync(email);
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(new { userId, email, displayName = "X" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateServiceToken(AccessApiFactory.InternalClientId));
        request.Headers.Add("X-Tenant-Id", _tenant);

        using var response = await _client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
