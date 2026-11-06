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

    private static async Task<MeDto> MeAsync(HttpClient client, string tenant, string user = DemoUsers.Maria)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/access/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateToken(user, tenant, "manager"));
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
    public async Task Parallel_first_reads_store_one_tenant_row_and_all_succeed()
    {
        // Review M1: several first reads of an unnamed tenant at once (two Access instances, three callers each) all find the
        // organization and try to insert the row; the losers' primary key conflict must not turn into a 500.
        var keycloak = await lazyKeycloak.GetAsync();
        var tenant = $"n{Guid.NewGuid():N}"[..12];
        await keycloak.CreateOrganizationAsync(tenant, "Parallel Partners");
        await infra.CreateTenantAsync(tenant);

        await using var first = new AccessApiFactory(infra, keycloak);
        await using var second = new AccessApiFactory(infra, keycloak);
        using var a = first.CreateClient();
        using var b = second.CreateClient();
        // A different caller each: identical GETs would be coalesced into one execution (and served from the 1 s cache).
        var reads = Enumerable.Range(0, 6).Select(i => MeAsync(i % 2 == 0 ? a : b, tenant, $"parallel-{i}")).ToList();

        var results = await Task.WhenAll(reads);

        results.ShouldAllBe(me => me.TenantName == "Parallel Partners");
        await using var db = new AccessDbContext(new DbContextOptionsBuilder<AccessDbContext>().UseNpgsql(infra.DbConnectionString).Options);
        (await db.Tenants.CountAsync(t => t.Id == tenant, Ct)).ShouldBe(1);
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
