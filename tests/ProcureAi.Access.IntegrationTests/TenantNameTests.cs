using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Infrastructure;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>
/// A tenant that the demo seed did not create (a real deployment) gets its name from its Keycloak organization the first time
/// Access needs it, and keeps it as a <c>tenants</c> row (plan 07 carry-over 14). Each test uses a fresh tenant and organization.
/// </summary>
[Collection(AccessInfraCollection.Name)]
public sealed class TenantNameTests(AccessInfraFixture infra, LazyKeycloak lazyKeycloak)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<MeDto> MeAsync(HttpClient client, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/access/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateToken(DemoUsers.Maria, tenant, "manager"));
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<MeDto>(Json, Ct))!;
    }

    private async Task<string?> StoredNameAsync(string tenant)
    {
        await using var db = new AccessDbContext(new DbContextOptionsBuilder<AccessDbContext>().UseNpgsql(infra.DbConnectionString).Options);
        return await db.Tenants.Where(t => t.Id == tenant).Select(t => t.Name).SingleOrDefaultAsync(Ct);
    }

    [Fact]
    public async Task Tenant_name_comes_from_access_without_demo_seed()
    {
        var keycloak = await lazyKeycloak.GetAsync();
        var tenant = $"n{Guid.NewGuid():N}"[..12];
        await keycloak.CreateOrganizationAsync(tenant, "Initech Holdings");
        await infra.CreateTenantAsync(tenant);
        (await StoredNameAsync(tenant)).ShouldBeNull("the test starts like a non-demo deployment: no tenants row");

        await using (var factory = new AccessApiFactory(infra, keycloak))
        {
            using var client = factory.CreateClient();
            (await MeAsync(client, tenant)).TenantName.ShouldBe("Initech Holdings");
        }

        (await StoredNameAsync(tenant)).ShouldBe("Initech Holdings", "the name is kept, so later reads need no Keycloak call");

        // Kept: an Access without a reachable Keycloak still shows the name.
        await using (var offline = new AccessApiFactory(infra))
        {
            using var client = offline.CreateClient();
            (await MeAsync(client, tenant)).TenantName.ShouldBe("Initech Holdings");
        }
    }

    [Fact]
    public async Task Tenant_without_an_organization_is_shown_under_its_id_and_not_stored()
    {
        var keycloak = await lazyKeycloak.GetAsync();
        var tenant = $"n{Guid.NewGuid():N}"[..12];
        await infra.CreateTenantAsync(tenant);

        await using (var factory = new AccessApiFactory(infra, keycloak))
        {
            using var client = factory.CreateClient();
            (await MeAsync(client, tenant)).TenantName.ShouldBe(tenant);
        }

        // Keycloak unreachable: also the ID, and the request does not fail.
        await using (var offline = new AccessApiFactory(infra))
        {
            using var client = offline.CreateClient();
            (await MeAsync(client, tenant)).TenantName.ShouldBe(tenant);
        }

        (await StoredNameAsync(tenant)).ShouldBeNull("a fallback is never stored: the organization may be created later");
    }
}
