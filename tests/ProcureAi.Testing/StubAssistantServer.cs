using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProcureAi.Testing;

/// <summary>What the stub Assistant saw in one internal scan read.</summary>
public sealed record StubScanCall(Guid ScanId, string? Tenant, string? CallingService, bool HasBearer);

/// <summary>
/// A real Kestrel server playing the Assistant's internal scan read (<c>GET api/v1/assistant/internal/invoice-scans/{id}</c>) for Invoicing.
/// Scans are set per test (<see cref="AddScan"/>); like the real service it answers 404 unless the scan belongs to the tenant in
/// <c>X-Tenant-Id</c>. A scan added with <c>leak</c> is answered to every tenant, with its real tenant in the answer (an Assistant that
/// leaks another tenant's scan), so the caller's own tenant check can be tested. <see cref="Settings"/> points a service at it.
/// </summary>
public sealed class StubAssistantServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<Guid, (string Tenant, bool Leak, object Scan)> _scans = new();
    private readonly ConcurrentQueue<StubScanCall> _calls = new();

    private StubAssistantServer(WebApplication app) => _app = app;

    public string Url { get; private set; } = "";

    public IReadOnlyList<StubScanCall> Calls => [.. _calls];

    /// <summary>Configuration that makes a service find the Assistant here (Aspire service discovery's configuration source).</summary>
    public Dictionary<string, string?> Settings => new() { ["Services:assistant-api:http:0"] = Url };

    /// <summary>Stores a scan (the <c>InvoiceScanDto</c> shape) of <paramref name="tenant"/>; with <paramref name="leak"/> any tenant gets it.</summary>
    public void AddScan(string tenant, Guid id, object scan, bool leak = false) => _scans[id] = (tenant, leak, scan);

    public static async Task<StubAssistantServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new StubAssistantServer(app);

        app.MapGet("/api/v1/assistant/internal/invoice-scans/{id:guid}", (HttpContext http, Guid id) =>
        {
            var tenant = Header(http, "X-Tenant-Id");
            stub._calls.Enqueue(new StubScanCall(
                id, tenant, Header(http, "X-Calling-Service"),
                http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)));
            if (!stub._scans.TryGetValue(id, out var stored) || (stored.Tenant != tenant && !stored.Leak))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"Invoice scan {id} was not found.");
            }

            return Results.Json(new { id, tenantId = stored.Tenant, userId = "fiona", createdAt = DateTimeOffset.UtcNow, scan = stored.Scan },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });

        await app.StartAsync();
        stub.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return stub;
    }

    private static string? Header(HttpContext http, string name) =>
        http.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
