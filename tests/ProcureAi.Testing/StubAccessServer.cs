using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Testing;

/// <summary>What the stub Access (or its token endpoint) saw in one request.</summary>
public sealed record StubAccessCall(string Path, string? Tenant, string? CallingService, string? OnBehalfOf, bool HasBearer);

/// <summary>
/// A real Kestrel server playing the Access service (and Keycloak's token endpoint) for the services' Access clients.
/// Each tenant has its own role to permission matrix (the default one until a test sets another), so tests that use a
/// tenant of their own do not depend on each other. <see cref="Settings"/> points a service at it.
/// </summary>
public sealed class StubAccessServer : IAsyncDisposable
{
    private static readonly string[] Basics =
    [
        Permissions.RequestsRead, Permissions.RequestsCreate, Permissions.AssistantUse,
        Permissions.OrdersRead, Permissions.InvoicesRead, Permissions.BudgetsRead,
        Permissions.OrdersReceive, Permissions.InvoicesCreate,
    ];

    private static readonly Dictionary<string, string[]> DefaultMatrix = new(StringComparer.Ordinal)
    {
        ["requester"] = Basics,
        ["manager"] = [.. Basics, Permissions.RequestsApprove],
        ["department-head"] = [.. Basics, Permissions.RequestsApprove],
        ["finance"] = [.. Basics, Permissions.RequestsApprove, Permissions.InvoicesReleasePayment, Permissions.BudgetsManage],
        ["admin"] =
        [
            Permissions.RequestsRead, Permissions.AssistantUse, Permissions.OrdersRead, Permissions.InvoicesRead,
            Permissions.BudgetsRead, Permissions.BudgetsManage, Permissions.AdminAuthorization,
            Permissions.AdminUsers, Permissions.AdminPolicies, Permissions.AdminSystem,
        ],
    };

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Dictionary<string, string[]>> _matrices = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<StubAccessCall> _calls = new();
    private int _effectiveCalls;
    private int _approverCalls;

    private StubAccessServer(WebApplication app) => _app = app;

    public string Url { get; private set; } = "";

    /// <summary>When true, Access answers 503 (the token endpoint keeps working).</summary>
    public volatile bool Down;

    /// <summary>Makes each effective-access answer slow, so concurrent callers overlap.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Cost center codes the stub answers 404 for.</summary>
    public ConcurrentDictionary<string, bool> MissingCostCenters { get; } = new(StringComparer.Ordinal);

    public int EffectiveCalls => Volatile.Read(ref _effectiveCalls);

    public int ApproverCalls => Volatile.Read(ref _approverCalls);

    public IReadOnlyList<StubAccessCall> Calls => [.. _calls];

    /// <summary>Configuration that makes a service find Access here and log in to this server's token endpoint.</summary>
    public Dictionary<string, string?> Settings => new()
    {
        ["Services:access-api:http:0"] = Url,
        ["Auth:Internal:TokenUrl"] = Url + "/token",
        ["Auth:Internal:ClientId"] = "procure-internal",
        ["Auth:Internal:ClientSecret"] = "stub-secret",
    };

    /// <summary>Replaces the matrix of one tenant with exactly these roles.</summary>
    public void SetMatrix(string tenantId, Dictionary<string, string[]> roles) => _matrices[tenantId] = roles;

    public static async Task<StubAccessServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new StubAccessServer(app);

        app.MapPost("/token", () => Results.Json(new { access_token = "stub-token", expires_in = 300, token_type = "Bearer" }));
        app.MapGet("/api/v1/access/internal/effective", async (HttpContext http, string userId, string[] roles) =>
        {
            Interlocked.Increment(ref stub._effectiveCalls);
            stub.Record(http);
            if (stub.Delay > TimeSpan.Zero)
            {
                await Task.Delay(stub.Delay);
            }

            if (stub.Down)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var tenant = http.Request.Headers["X-Tenant-Id"].ToString();
            var matrix = stub._matrices.GetValueOrDefault(tenant) ?? DefaultMatrix;
            var wanted = roles.SelectMany(r => r.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
            var permissions = matrix.Where(r => wanted.Contains(r.Key)).SelectMany(r => r.Value).Distinct().Order(StringComparer.Ordinal);
            return Results.Json(new { permissions, costCenterCodes = new[] { "CC-100" }, approvalLimit = 50000m });
        });
        app.MapGet("/api/v1/access/internal/cost-centers/{code}/approvers", (HttpContext http, string code) =>
        {
            Interlocked.Increment(ref stub._approverCalls);
            stub.Record(http);
            if (stub.Down)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (stub.MissingCostCenters.ContainsKey(code))
            {
                return Results.NotFound();
            }

            // The manager's ID names the tenant and the code, so a test can tell whose answer it got.
            return Results.Json(new
            {
                costCenterCode = code,
                departmentCode = "IT",
                manager = new { userId = $"{http.Request.Headers["X-Tenant-Id"]}/{code}", displayName = "Mia Manager", approvalLimit = 50000m, canApprove = true },
                departmentHead = (object?)null,
                finance = Array.Empty<object>(),
            });
        });

        await app.StartAsync();
        stub.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return stub;
    }

    private void Record(HttpContext http) =>
        _calls.Enqueue(new StubAccessCall(
            http.Request.Path.Value!, Header(http, "X-Tenant-Id"), Header(http, "X-Calling-Service"),
            Header(http, "X-On-Behalf-Of-User"), http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)));

    private static string? Header(HttpContext http, string name) =>
        http.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
