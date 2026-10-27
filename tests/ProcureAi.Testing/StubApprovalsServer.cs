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

/// <summary>What the stub Approvals saw in one preview request.</summary>
public sealed record StubPreviewCall(
    string? Tenant, string? CallingService, string? OnBehalfOf, bool HasBearer, string CostCenter, decimal Amount, string RequesterId);

/// <summary>
/// A real Kestrel server playing the Approvals service's internal preview (<c>GET api/v1/approvals/internal/preview</c>) for
/// the Requests service's pre-check before a submission. Every tenant gets "possible" until a test says otherwise, so tests
/// with a tenant of their own do not depend on each other. <see cref="Settings"/> points a service at it.
/// </summary>
public sealed class StubApprovalsServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, string> _notPossible = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _down = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<StubPreviewCall> _calls = new();

    private StubApprovalsServer(WebApplication app) => _app = app;

    public string Url { get; private set; } = "";

    public IReadOnlyList<StubPreviewCall> Calls => [.. _calls];

    /// <summary>Configuration that makes a service find Approvals here (Aspire service discovery's configuration source).</summary>
    public Dictionary<string, string?> Settings => new() { ["Services:approvals-api:http:0"] = Url };

    /// <summary>Previews for the tenant answer "not possible" with <paramref name="reason"/>.</summary>
    public void SetNotPossible(string tenantId, string reason) => _notPossible[tenantId] = reason;

    /// <summary>Previews for the tenant answer 503, as Approvals does when Access cannot be reached.</summary>
    public void SetDown(string tenantId) => _down[tenantId] = true;

    public static async Task<StubApprovalsServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new StubApprovalsServer(app);

        app.MapGet("/api/v1/approvals/internal/preview", (HttpContext http, string costCenter, string amount, string requesterId) =>
        {
            var tenant = Header(http, "X-Tenant-Id");
            stub._calls.Enqueue(new StubPreviewCall(
                tenant, Header(http, "X-Calling-Service"), Header(http, "X-On-Behalf-Of-User"),
                http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal),
                costCenter, decimal.Parse(amount, CultureInfo.InvariantCulture), requesterId));
            if (tenant is not null && stub._down.ContainsKey(tenant))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (tenant is not null && stub._notPossible.TryGetValue(tenant, out var reason))
            {
                return Results.Json(new { possible = false, reason, steps = Array.Empty<object>() });
            }

            return Results.Json(new
            {
                possible = true,
                reason = (string?)null,
                steps = new[] { new { stepNo = 1, level = "Manager", approverNames = new[] { "Mia Manager" } } },
            });
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
