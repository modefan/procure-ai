using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProcureAi.Testing;

/// <summary>One item of the stub catalog. The price is Rand excl. VAT.</summary>
public sealed record StubCatalogItem(string Sku, string Description, decimal UnitPriceExclVat, bool Active = true);

/// <summary>What the stub Suppliers saw in one item lookup.</summary>
public sealed record StubCatalogCall(string? Tenant, string Code, IReadOnlyList<string> Skus, string? CallingService, bool HasBearer);

/// <summary>
/// A real Kestrel server playing the Suppliers service's internal item lookup
/// (<c>GET api/v1/suppliers/internal/{code}/items?skus=A,B</c>) for the Requests service. The catalog is in memory and set per
/// test: for one tenant (<see cref="SetCatalog"/>) or, until a tenant has its own, the same for every tenant (<see cref="SetDefaultCatalog"/>).
/// Like the real service it answers 404 for an unknown supplier and lists only the asked SKUs that exist, active or not.
/// <see cref="Settings"/> points a service at it.
/// </summary>
public sealed class StubSuppliersServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<(string Tenant, string Code), StubSupplier> _byTenant = new();
    private readonly ConcurrentDictionary<string, StubSupplier> _default = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _down = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<StubCatalogCall> _calls = new();

    private StubSuppliersServer(WebApplication app) => _app = app;

    private sealed record StubSupplier(string Name, bool Active, IReadOnlyDictionary<string, StubCatalogItem> Items);

    /// <summary>The supplier every tenant has until a test says otherwise (the code of the demo hardware supplier).</summary>
    public const string DefaultSupplier = "contoso-hardware";

    /// <summary>What <see cref="DefaultSupplier"/> offers by default: the SKUs the tests pick lines from.</summary>
    public static readonly StubCatalogItem[] DefaultItems =
    [
        new("LAP-1", "Laptop", 25_200m),
        new("LAP-14", "Laptop 14\"", 25_200m),
        new("MON-1", "Monitor", 3_000m),
        new("A", "a", 10m),
        new("X", "x", 199_000m),
        new("DISC-01", "Discontinued webcam", 990m, Active: false),
    ];

    public string Url { get; private set; } = "";

    public IReadOnlyList<StubCatalogCall> Calls => [.. _calls];

    /// <summary>Configuration that makes a service find Suppliers here (Aspire service discovery's configuration source).</summary>
    public Dictionary<string, string?> Settings => new() { ["Services:suppliers-api:http:0"] = Url };

    /// <summary>Gives the tenant its own supplier <paramref name="code"/> (replacing the default for that code).</summary>
    public void SetCatalog(string tenantId, string code, bool active, params StubCatalogItem[] items) =>
        _byTenant[(tenantId, code)] = Build(code, active, items);

    /// <summary>The supplier <paramref name="code"/> every tenant without its own sees.</summary>
    public void SetDefaultCatalog(string code, bool active, params StubCatalogItem[] items) => _default[code] = Build(code, active, items);

    /// <summary>Lookups for the tenant answer 503, as when Suppliers is unavailable.</summary>
    public void SetDown(string tenantId) => _down[tenantId] = true;

    public void SetUp(string tenantId) => _down.TryRemove(tenantId, out _);

    public static async Task<StubSuppliersServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new StubSuppliersServer(app);
        stub.SetDefaultCatalog(DefaultSupplier, true, DefaultItems);

        app.MapGet("/api/v1/suppliers/internal/{code}/items", (HttpContext http, string code, string skus) =>
        {
            var tenant = Header(http, "X-Tenant-Id");
            var asked = skus.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            stub._calls.Enqueue(new StubCatalogCall(
                tenant, code, asked, Header(http, "X-Calling-Service"),
                http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)));
            if (tenant is not null && stub._down.ContainsKey(tenant))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var supplier = tenant is not null && stub._byTenant.TryGetValue((tenant, code), out var own)
                ? own
                : stub._default.GetValueOrDefault(code);
            if (supplier is null)
            {
                return Results.NotFound();
            }

            return Results.Json(new
            {
                code,
                name = supplier.Name,
                active = supplier.Active,
                items = asked.Distinct(StringComparer.Ordinal)
                    .Where(supplier.Items.ContainsKey)
                    .Select(sku => supplier.Items[sku])
                    .Select(i => new
                    {
                        sku = i.Sku,
                        description = i.Description,
                        unit = "each",
                        unitPriceExclVat = i.UnitPriceExclVat,
                        unitPriceDisplay = i.UnitPriceExclVat.ToString("N2", CultureInfo.GetCultureInfo("en-ZA")),
                        active = i.Active,
                    })
                    .ToArray(),
            });
        });

        await app.StartAsync();
        stub.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return stub;
    }

    private static StubSupplier Build(string code, bool active, StubCatalogItem[] items) =>
        new($"Supplier {code}", active, items.ToDictionary(i => i.Sku, StringComparer.Ordinal));

    private static string? Header(HttpContext http, string name) =>
        http.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
