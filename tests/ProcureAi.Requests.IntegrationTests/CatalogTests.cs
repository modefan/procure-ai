using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcureAi.Contracts;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Domain;
using ProcureAi.Testing;
using Shouldly;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// Request lines come from the Suppliers catalog: description and price are the catalog's, an unknown supplier or SKU is refused
/// (422), Suppliers being down is a 503 unless the last lookup is still cached (fail-safe), and a draft line keeps its locked price.
/// Real Program, Postgres, RabbitMQ and Redis; a stub Suppliers, Approvals and Access.
/// </summary>
[Collection(InfraCollection.Name)]
public sealed class CatalogTests(InfraFixture infra) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private const string Requests = "/api/v1/requests";
    private const string Supplier = "northwind";

    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(30);

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(infra);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string NewTenant(string name) => $"{name}-{Guid.NewGuid():N}"[..24];

    private static HttpRequestMessage Req(HttpMethod method, string url, string tenant, object? body = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", tenant);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (method == HttpMethod.Post)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return request;
    }

    private static object Body(string supplier, params (string Sku, int Quantity)[] lines) => new
    {
        title = "Catalog request",
        costCenterCode = "CC-100",
        supplierId = supplier,
        lines = lines.Select(l => new { sku = l.Sku, quantity = l.Quantity }).ToArray(),
    };

    private Task<HttpResponseMessage> PostAsync(HttpClient client, string tenant, object body) =>
        client.SendAsync(Req(HttpMethod.Post, Requests, tenant, body), Ct);

    private static async Task<RequestDto> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    private static async Task<string> ProblemTitleAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return problem.GetProperty("title").GetString()!;
    }

    [Fact]
    public async Task Create_takes_description_and_price_from_catalog()
    {
        var tenant = NewTenant("catalog");
        infra.Suppliers.SetCatalog(tenant, Supplier, true, new StubCatalogItem("CAB-1", "Network cable 5 m", 123.45m));

        var dto = await ReadAsync(await PostAsync(_client, tenant, Body(Supplier, ("CAB-1", 4))), HttpStatusCode.Created);

        var line = dto.Lines.ShouldHaveSingleItem();
        line.Sku.ShouldBe("CAB-1");
        line.Description.ShouldBe("Network cable 5 m");
        line.UnitPriceExclVat.ShouldBe(123.45m);
        line.Quantity.ShouldBe(4);
        dto.TotalExclVat.ShouldBe(493.80m);
        // Suppliers was asked as Requests (its own token), for this tenant and these SKUs.
        var call = infra.Suppliers.Calls.Where(c => c.Tenant == tenant).ShouldHaveSingleItem();
        call.Code.ShouldBe(Supplier);
        call.Skus.ShouldBe(["CAB-1"]);
        call.CallingService.ShouldBe("requests-api");
        call.HasBearer.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_sku_422_names_it()
    {
        var tenant = NewTenant("unksku");
        infra.Suppliers.SetCatalog(tenant, Supplier, true, new StubCatalogItem("CAB-1", "Network cable", 10m));

        var response = await PostAsync(_client, tenant, Body(Supplier, ("CAB-1", 1), ("NOPE-9", 1)));

        response.StatusCode.ShouldBe((HttpStatusCode)422, await response.Content.ReadAsStringAsync(Ct));
        (await ProblemTitleAsync(response)).ShouldContain("NOPE-9");
    }

    [Fact]
    public async Task Inactive_sku_422_names_it()
    {
        var tenant = NewTenant("inasku");
        infra.Suppliers.SetCatalog(tenant, Supplier, true, new StubCatalogItem("OLD-1", "Old cable", 10m, Active: false));

        var response = await PostAsync(_client, tenant, Body(Supplier, ("OLD-1", 1)));

        response.StatusCode.ShouldBe((HttpStatusCode)422, await response.Content.ReadAsStringAsync(Ct));
        (await ProblemTitleAsync(response)).ShouldContain("OLD-1");
    }

    [Fact]
    public async Task Unknown_supplier_422()
    {
        var tenant = NewTenant("unksup");

        var response = await PostAsync(_client, tenant, Body("no-such-supplier", ("CAB-1", 1)));

        response.StatusCode.ShouldBe((HttpStatusCode)422, await response.Content.ReadAsStringAsync(Ct));
        (await ProblemTitleAsync(response)).ShouldContain("Unknown supplier");
    }

    [Fact]
    public async Task Inactive_supplier_422()
    {
        var tenant = NewTenant("inasup");
        infra.Suppliers.SetCatalog(tenant, Supplier, false, new StubCatalogItem("CAB-1", "Network cable", 10m));

        var response = await PostAsync(_client, tenant, Body(Supplier, ("CAB-1", 1)));

        response.StatusCode.ShouldBe((HttpStatusCode)422, await response.Content.ReadAsStringAsync(Ct));
        (await ProblemTitleAsync(response)).ShouldContain("not active");
    }

    [Fact]
    public async Task A_catalog_price_typed_by_the_requester_is_ignored()
    {
        var tenant = NewTenant("typedprice");
        infra.Suppliers.SetCatalog(tenant, Supplier, true, new StubCatalogItem("CAB-1", "Network cable", 10m));
        var body = new
        {
            title = "Cheap",
            costCenterCode = "CC-100",
            supplierId = Supplier,
            lines = new[] { new { sku = "CAB-1", quantity = 1, description = "Gold cable", unitPriceExclVat = 0.01m } },
        };

        var dto = await ReadAsync(await PostAsync(_client, tenant, body), HttpStatusCode.Created);

        var line = dto.Lines.ShouldHaveSingleItem();
        line.Description.ShouldBe("Network cable");
        line.UnitPriceExclVat.ShouldBe(10m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Suppliers_down_and_nothing_cached_503_with_retry_after(bool unreachable)
    {
        var tenant = NewTenant("supdown");
        infra.Suppliers.SetDown(tenant);
        // Unreachable: nothing listens at the address (connection refused), rather than Suppliers answering 503.
        await using var factory = unreachable
            ? new ApiFactory(infra, settings: new Dictionary<string, string?> { ["Services:suppliers-api:http:0"] = "http://127.0.0.1:9" })
            : null;
        using var client = factory?.CreateClient() ?? _factory.CreateClient();

        var response = await PostAsync(client, tenant, Body(Supplier, ("CAB-1", 1)));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.RetryAfter.ShouldNotBeNull();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Suppliers_down_after_a_lookup_uses_cached_price()
    {
        // Answers are fresh for 1 s, so the second request really asks again, fails, and gets the last good answer (fail-safe).
        await using var factory = new ApiFactory(infra, settings: new Dictionary<string, string?>
        {
            ["SuppliersClient:CacheDuration"] = "00:00:01",
        });
        using var client = factory.CreateClient();
        var tenant = NewTenant("supcache");
        infra.Suppliers.SetCatalog(tenant, Supplier, true, new StubCatalogItem("CAB-1", "Network cable", 10m));
        (await ReadAsync(await PostAsync(client, tenant, Body(Supplier, ("CAB-1", 1))), HttpStatusCode.Created))
            .Lines.ShouldHaveSingleItem().UnitPriceExclVat.ShouldBe(10m);

        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        infra.Suppliers.SetDown(tenant);
        var callsBefore = infra.Suppliers.Calls.Count(c => c.Tenant == tenant);

        var dto = await ReadAsync(await PostAsync(client, tenant, Body(Supplier, ("CAB-1", 2))), HttpStatusCode.Created);

        dto.Lines.ShouldHaveSingleItem().UnitPriceExclVat.ShouldBe(10m);
        // It did try Suppliers (the entry had expired): the answer is the stale one, not a fresh one.
        infra.Suppliers.Calls.Count(c => c.Tenant == tenant).ShouldBeGreaterThan(callsBefore);
    }

    [Fact]
    public async Task Identical_lookups_in_a_row_ask_Suppliers_once()
    {
        var tenant = NewTenant("supone");
        infra.Suppliers.SetCatalog(tenant, Supplier, true, new StubCatalogItem("CAB-1", "Network cable", 10m));

        await ReadAsync(await PostAsync(_client, tenant, Body(Supplier, ("CAB-1", 1))), HttpStatusCode.Created);
        await ReadAsync(await PostAsync(_client, tenant, Body(Supplier, ("CAB-1", 5))), HttpStatusCode.Created);

        infra.Suppliers.Calls.Count(c => c.Tenant == tenant).ShouldBe(1);
    }

    [Fact]
    public async Task Price_change_in_catalog_does_not_change_existing_draft_line()
    {
        var tenant = NewTenant("lockprice");
        infra.Suppliers.SetCatalog(
            tenant, Supplier, true, new StubCatalogItem("CAB-1", "Network cable", 100m), new StubCatalogItem("CAB-2", "Patch panel", 50m));
        var draft = await ReadAsync(await PostAsync(_client, tenant, Body(Supplier, ("CAB-1", 1))), HttpStatusCode.Created);

        // The catalog now charges more (and says something else) for CAB-1; the draft keeps the price it was raised at.
        infra.Suppliers.SetCatalog(
            tenant, Supplier, true, new StubCatalogItem("CAB-1", "Network cable (new)", 200m), new StubCatalogItem("CAB-2", "Patch panel", 50m));
        var updated = await ReadAsync(
            await _client.SendAsync(
                Req(HttpMethod.Put, $"{Requests}/{draft.Id}", tenant, new
                {
                    title = "Catalog request",
                    lines = new[] { new { sku = "CAB-1", quantity = 3 }, new { sku = "CAB-2", quantity = 1 } },
                }, $"\"{draft.Version}\""), Ct),
            HttpStatusCode.OK);

        updated.Lines.Count.ShouldBe(2);
        var kept = updated.Lines.Single(l => l.Sku == "CAB-1");
        kept.UnitPriceExclVat.ShouldBe(100m);
        kept.Description.ShouldBe("Network cable");
        kept.Quantity.ShouldBe(3);
        updated.Lines.Single(l => l.Sku == "CAB-2").UnitPriceExclVat.ShouldBe(50m);
    }

    [Fact]
    public async Task Update_with_a_sku_new_to_the_draft_that_the_catalog_does_not_have_is_422()
    {
        var tenant = NewTenant("updunk");
        infra.Suppliers.SetCatalog(tenant, Supplier, true, new StubCatalogItem("CAB-1", "Network cable", 100m));
        var draft = await ReadAsync(await PostAsync(_client, tenant, Body(Supplier, ("CAB-1", 1))), HttpStatusCode.Created);

        var response = await _client.SendAsync(
            Req(HttpMethod.Put, $"{Requests}/{draft.Id}", tenant, new
            {
                title = "Catalog request",
                lines = new[] { new { sku = "CAB-1", quantity = 1 }, new { sku = "GHOST-1", quantity = 1 } },
            }, $"\"{draft.Version}\""), Ct);

        response.StatusCode.ShouldBe((HttpStatusCode)422, await response.Content.ReadAsStringAsync(Ct));
        (await ProblemTitleAsync(response)).ShouldContain("GHOST-1");
    }

    [Fact]
    public async Task Submitted_event_carries_supplier_and_lines()
    {
        var tenant = NewTenant("evlines");
        infra.Suppliers.SetCatalog(
            tenant, Supplier, true, new StubCatalogItem("CAB-1", "Network cable", 100m), new StubCatalogItem("CAB-2", "Patch panel", 50m));
        var draft = await ReadAsync(
            await PostAsync(_client, tenant, Body(Supplier, ("CAB-1", 2), ("CAB-2", 1))), HttpStatusCode.Created);
        await using var recorder = await RabbitRecorder.StartAsync(infra.RabbitConnectionString, [Exchanges.Requests], []);

        var response = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{draft.Id}/submit", tenant, ifMatch: $"\"{draft.Version}\""), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));

        var messages = await recorder.WaitForAsync(draft.Id, "RequestSubmitted", MessageTimeout);
        var body = messages.Single(m => m.Kind == "RequestSubmitted").Body;
        body.GetProperty("supplierId").GetString().ShouldBe(Supplier);
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        lines.Count.ShouldBe(2);
        lines[0].GetProperty("sku").GetString().ShouldBe("CAB-1");
        lines[0].GetProperty("description").GetString().ShouldBe("Network cable");
        lines[0].GetProperty("quantity").GetInt32().ShouldBe(2);
        lines[0].GetProperty("unitPriceExclVat").GetDecimal().ShouldBe(100m);
        lines[1].GetProperty("sku").GetString().ShouldBe("CAB-2");
        lines[1].GetProperty("unitPriceExclVat").GetDecimal().ShouldBe(50m);
        body.GetProperty("totalInclVat").GetDecimal().ShouldBe(287.50m);
    }
}
