using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Audit.Application.Cdc;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Contracts;
using Shouldly;
using Wolverine;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// Review focus 1 of plan 06: the data-change log is the admin's, but a user caller (even with <c>admin.system</c>) must only
/// ever see the rows of their own tenant; only the admin API key without <c>X-Tenant-Id</c> sees all tenants.
/// </summary>
[Collection(AuditCollection.Name)]
public sealed class DataChangeSearchTenantTests(AuditApp app)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_user_search_sees_only_own_tenant()
    {
        var (database, tenantA, tenantB, changes) = await SeedAsync();

        // The key without a tenant goes first: its cached answer must not be served to the user (the cache is keyed by tenant).
        (await SearchAsync(AuditApp.GetWithApiKey(Log(database), tenant: null))).Total.ShouldBe(2);

        foreach (var (tenant, own) in new[] { (tenantA, changes.A), (tenantB, changes.B) })
        {
            var page = await SearchAsync(AuditApp.Get(Log(database), TestAuth.CreateToken(DemoUsers.Admin, tenant, "admin")));
            page.Items.Select(c => c.Id).ShouldBe([own]);
            page.Total.ShouldBe(1);
            page.Items.ShouldAllBe(c => c.TenantId == tenant);
        }

        // Asking for the other tenant's user, table or a tenant parameter does not widen the view.
        var snoop = await SearchAsync(AuditApp.Get(
            Log($"database={database}&tenantId={tenantB}"), TestAuth.CreateToken(DemoUsers.Admin, tenantA, "admin")));
        snoop.Items.Select(c => c.Id).ShouldBe([changes.A]);
    }

    [Fact]
    public async Task Admin_key_without_tenant_sees_all()
    {
        var (database, _, _, changes) = await SeedAsync();

        var all = await SearchAsync(AuditApp.GetWithApiKey(Log(database), tenant: null));
        all.Items.Select(c => c.Id).ShouldBe([changes.A, changes.B], ignoreOrder: true);

        var one = await SearchAsync(AuditApp.GetWithApiKey(Log(database), tenant: all.Items[0].TenantId));
        one.Items.ShouldHaveSingleItem().TenantId.ShouldBe(all.Items[0].TenantId);
    }

    private async Task<(string Database, string TenantA, string TenantB, (string A, string B) Changes)> SeedAsync()
    {
        var database = $"syn{Guid.NewGuid():N}"[..12];
        var tenantA = AuditApp.NewTenant();
        var tenantB = AuditApp.NewTenant();
        var a = SyntheticTransaction.New(database, tenantA);
        var b = SyntheticTransaction.New(database, tenantB);
        foreach (var tx in new[] { a, b })
        {
            await app.Bus.InvokeAsync(JsonSerializer.Deserialize<DebeziumChange>(tx.ChangeJson())!);
        }

        await app.WaitForAsync<DataChange>(q => q.Where(c => c.Database == database), found => found.Count == 2);
        return (database, tenantA, tenantB, (a.ChangeId, b.ChangeId));
    }

    private static string Log(string query) => $"/api/v1/audit/data-changes?{(query.Contains('=') ? query : $"database={query}")}";

    private async Task<PagedResult<DataChangeDto>> SearchAsync(HttpRequestMessage request)
    {
        using var response = await app.Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PagedResult<DataChangeDto>>(Json, Ct)).ShouldNotBeNull();
    }
}
