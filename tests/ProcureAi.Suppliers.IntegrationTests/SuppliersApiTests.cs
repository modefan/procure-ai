using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Suppliers.Application.Dtos;
using Shouldly;

namespace ProcureAi.Suppliers.IntegrationTests;

/// <summary>The Suppliers API over HTTP against the demo-seeded MongoDB: tenant isolation, the internal lookup, caching and the seed.</summary>
[Collection(SuppliersCollection.Name)]
public sealed class SuppliersApiTests(SuppliersApp app)
{
    private const string Base = "/api/v1/suppliers";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> GetAsync(string url, string token, string? tenantHeader = null) =>
        app.Client.SendAsync(SuppliersApp.Request(url, token, tenantHeader), Ct);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    [Fact]
    public async Task Acme_lists_three_suppliers_globex_one()
    {
        var acme = await ReadAsync<PagedResult<SupplierSummaryDto>>(await GetAsync(Base, SuppliersApp.UserToken(SuppliersApp.Acme)));
        var globex = await ReadAsync<PagedResult<SupplierSummaryDto>>(await GetAsync(Base, SuppliersApp.UserToken(SuppliersApp.Globex)));

        acme.Items.Select(s => s.Code).ShouldBe(["adatum-software", "contoso-hardware", "fabrikam-office"]);
        acme.Total.ShouldBe(3);
        acme.Items.Single(s => s.Code == "contoso-hardware").ItemCount.ShouldBe(5);
        globex.Items.Select(s => s.Code).ShouldBe(["northwind-traders"]);
        globex.Total.ShouldBe(1);
    }

    [Fact]
    public async Task Search_matches_code_or_name_without_regex_surprises()
    {
        var token = SuppliersApp.UserToken(SuppliersApp.Acme);

        var office = await ReadAsync<PagedResult<SupplierSummaryDto>>(await GetAsync($"{Base}?search=OFFICE", token));
        var dot = await ReadAsync<PagedResult<SupplierSummaryDto>>(await GetAsync($"{Base}?search=.*", token));

        office.Items.Select(s => s.Code).ShouldBe(["fabrikam-office"]);
        dot.Items.ShouldBeEmpty("what the caller types is text, not a pattern");
    }

    [Fact]
    public async Task Supplier_detail_shows_only_active_items_but_the_item_search_shows_all_with_the_flag()
    {
        var token = SuppliersApp.UserToken(SuppliersApp.Acme);

        var supplier = await ReadAsync<SupplierDto>(await GetAsync($"{Base}/contoso-hardware", token));
        var items = await ReadAsync<PagedResult<CatalogItemDto>>(await GetAsync($"{Base}/contoso-hardware/items", token));
        var monitors = await ReadAsync<PagedResult<CatalogItemDto>>(await GetAsync($"{Base}/contoso-hardware/items?search=monitor", token));

        supplier.Items.Select(i => i.Sku).ShouldBe(["LAP-14", "MON-27", "DOCK-C", "KB-WL"]);
        items.Total.ShouldBe(5);
        items.Items.Single(i => i.Sku == "DISC-01").Active.ShouldBeFalse();
        var monitor = monitors.Items.ShouldHaveSingleItem();
        monitor.Sku.ShouldBe("MON-27");
        monitor.UnitPriceExclVat.ShouldBe(4_200m);
        monitor.UnitPriceDisplay.ShouldBe("R 4 200,00");
    }

    [Fact]
    public async Task Other_tenant_supplier_is_404()
    {
        var globexAsksForAcmeSupplier = await GetAsync($"{Base}/fabrikam-office", SuppliersApp.UserToken(SuppliersApp.Globex));
        var itemsToo = await GetAsync($"{Base}/fabrikam-office/items", SuppliersApp.UserToken(SuppliersApp.Globex));
        var unknown = await GetAsync($"{Base}/no-such-supplier", SuppliersApp.UserToken(SuppliersApp.Acme));

        globexAsksForAcmeSupplier.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        itemsToo.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Same_code_in_two_tenants_returns_each_tenants_prices()
    {
        var acme = await ReadAsync<PagedResult<CatalogItemDto>>(
            await GetAsync($"{Base}/contoso-hardware/items?search=LAP-14", SuppliersApp.UserToken(SuppliersApp.Acme)));
        var globex = await ReadAsync<PagedResult<CatalogItemDto>>(
            await GetAsync($"{Base}/northwind-traders/items?search=LAP-14", SuppliersApp.UserToken(SuppliersApp.Globex)));

        acme.Items.ShouldHaveSingleItem().UnitPriceExclVat.ShouldBe(25_200m);
        globex.Items.ShouldHaveSingleItem().UnitPriceExclVat.ShouldBe(24_900m);
    }

    [Fact]
    public async Task The_demo_tenant_has_its_own_copy_of_contoso()
    {
        var demo = await ReadAsync<PagedResult<SupplierSummaryDto>>(await GetAsync(Base, SuppliersApp.UserToken("demo")));

        demo.Items.Select(s => s.Code).ShouldBe(["contoso-hardware"]);
    }

    [Fact]
    public async Task Internal_items_returns_only_found_skus()
    {
        var response = await GetAsync(
            $"{Base}/internal/contoso-hardware/items?skus=LAP-14,DISC-01,NOPE,lap-14", SuppliersApp.ServiceToken(), SuppliersApp.Acme);

        var found = await ReadAsync<CatalogItemsDto>(response);

        found.Code.ShouldBe("contoso-hardware");
        found.Active.ShouldBeTrue();
        found.Items.Select(i => i.Sku).ShouldBe(["LAP-14", "DISC-01"], "unknown and wrongly-cased SKUs are simply missing");
        found.Items.Single(i => i.Sku == "DISC-01").Active.ShouldBeFalse();
    }

    [Fact]
    public async Task Internal_items_is_scoped_to_the_tenant_in_the_header()
    {
        var asGlobex = await GetAsync($"{Base}/internal/contoso-hardware/items?skus=LAP-14", SuppliersApp.ServiceToken(), SuppliersApp.Globex);
        var withKey = new HttpRequestMessage(HttpMethod.Get, $"{Base}/internal/northwind-traders/items?skus=LAP-14");
        withKey.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        withKey.Headers.Add("X-Tenant-Id", SuppliersApp.Globex);
        var keyResponse = await app.Client.SendAsync(withKey, Ct);

        asGlobex.StatusCode.ShouldBe(HttpStatusCode.NotFound, "globex has no contoso-hardware");
        (await ReadAsync<CatalogItemsDto>(keyResponse)).Items.ShouldHaveSingleItem().UnitPriceExclVat.ShouldBe(24_900m);
    }

    [Fact]
    public async Task Internal_endpoint_refuses_user_token_403()
    {
        var response = await GetAsync($"{Base}/internal/contoso-hardware/items?skus=LAP-14", SuppliersApp.UserToken(SuppliersApp.Acme, "admin"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Internal_endpoint_needs_a_tenant_and_valid_skus()
    {
        var noTenant = await GetAsync($"{Base}/internal/contoso-hardware/items?skus=LAP-14", SuppliersApp.ServiceToken());
        var none = await GetAsync($"{Base}/internal/contoso-hardware/items?skus=,", SuppliersApp.ServiceToken(), SuppliersApp.Acme);
        var tooMany = await GetAsync(
            $"{Base}/internal/contoso-hardware/items?skus={string.Join(',', Enumerable.Range(0, 101).Select(i => $"S{i}"))}",
            SuppliersApp.ServiceToken(), SuppliersApp.Acme);

        noTenant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        none.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Requires_requests_read_403()
    {
        // A role the matrix does not know has no permissions.
        var noPermission = await GetAsync(Base, SuppliersApp.UserToken(SuppliersApp.Acme, "nobody"));
        var anonymous = await app.Client.GetAsync(Base, Ct);

        noPermission.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bad_paging_is_a_400()
    {
        var token = SuppliersApp.UserToken(SuppliersApp.Acme);

        (await GetAsync($"{Base}?page=0", token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync($"{Base}?pageSize=101", token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync($"{Base}/contoso-hardware/items?pageSize=0", token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_is_micro_cached_and_coalesced()
    {
        // A user of its own, so the cache key (tenant, user, route, query) is new and nothing else cached this response.
        var token = SuppliersApp.UserToken(SuppliersApp.Globex, user: $"u-{Guid.NewGuid():N}");
        app.Commands.Clear();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => GetAsync($"{Base}/northwind-traders", token)));
        var again = await GetAsync($"{Base}/northwind-traders", token);

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Commands.Count("find", "suppliers").ShouldBe(1, "ten identical requests and one more within a second ran one MongoDB find");
    }

    [Fact]
    public async Task Seed_is_idempotent_when_migrate_runs_twice()
    {
        var suppliers = app.Infra.Db.GetCollection<BsonDocument>("suppliers");
        var before = await suppliers.Find(FilterDefinition<BsonDocument>.Empty).SortBy(d => d["_id"]).ToListAsync(Ct);

        await app.Infra.MigrateAsync();
        await app.Infra.MigrateAsync();
        var after = await suppliers.Find(FilterDefinition<BsonDocument>.Empty).SortBy(d => d["_id"]).ToListAsync(Ct);

        before.Select(d => d["_id"].AsString).ShouldBe(
            ["acme:adatum-software", "acme:contoso-hardware", "acme:fabrikam-office", "demo:contoso-hardware", "globex:northwind-traders"]);
        after.ShouldBe(before, "a second and third migrate run changed nothing");
    }

    [Fact]
    public async Task Prices_are_stored_as_decimal128_and_codes_are_unique_per_tenant()
    {
        var suppliers = app.Infra.Db.GetCollection<BsonDocument>("suppliers");

        var acme = await suppliers.Find(new BsonDocument("_id", "acme:contoso-hardware")).SingleAsync(Ct);
        var duplicate = new BsonDocument { { "_id", "acme:other-id" }, { "TenantId", "acme" }, { "Code", "contoso-hardware" } };

        acme["Items"].AsBsonArray[0]["UnitPriceExclVat"].BsonType.ShouldBe(BsonType.Decimal128);
        var thrown = await Should.ThrowAsync<MongoWriteException>(() => suppliers.InsertOneAsync(duplicate, cancellationToken: Ct));
        thrown.WriteError.Category.ShouldBe(ServerErrorCategory.DuplicateKey);
    }
}
