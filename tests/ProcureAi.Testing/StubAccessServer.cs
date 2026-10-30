using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Testing;

/// <summary>What the stub Access (or its token endpoint) saw in one request.</summary>
public sealed record StubAccessCall(string Path, string? Tenant, string? CallingService, string? OnBehalfOf, bool HasBearer);

/// <summary>What the stub Access saw in a registration request (a self-registered user joining the demo company).</summary>
public sealed record StubRegistration(
    string UserId, string? Tenant, string? OnBehalfOf, string? IdempotencyKey);

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

    /// <summary>
    /// Runs for each registration request before the stub answers 200 (to do what the real Access does, e.g. in Keycloak). When it
    /// throws, the stub answers 500. Without a hook the registration changes nothing.
    /// </summary>
    public Func<StubRegistration, Task>? OnRegister { get; set; }

    private readonly ConcurrentQueue<StubRegistration> _registrations = new();

    public IReadOnlyList<StubRegistration> Registrations => [.. _registrations];

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

    private readonly ConcurrentDictionary<(string Tenant, string UserId), string[]> _costCenters = new();

    /// <summary>The cost centers the stub says one user acts for (everyone else gets <c>CC-100</c>).</summary>
    public void SetCostCenters(string tenantId, string userId, params string[] codes) => _costCenters[(tenantId, userId)] = codes;

    private readonly ConcurrentDictionary<(string Tenant, string Code), CostCenterApprovers> _approvers = new();

    /// <summary>What the stub answers for the approvers of one cost center of one tenant (otherwise a manager named after tenant and code).</summary>
    public void SetApprovers(string tenantId, CostCenterApprovers approvers) =>
        _approvers[(tenantId, approvers.CostCenterCode)] = approvers;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _approversFailUntil = new(StringComparer.Ordinal);

    /// <summary>The approvers lookups of one tenant answer 503 for the next <paramref name="duration"/> (a short Access outage).</summary>
    public void FailApprovers(string tenantId, TimeSpan duration) => _approversFailUntil[tenantId] = DateTimeOffset.UtcNow + duration;

    private readonly ConcurrentDictionary<(string Tenant, string UserId), (string Name, string? Email)> _displayNames = new();
    private readonly ConcurrentDictionary<string, string> _tenantNames = new();
    private int _nameCalls;

    public int NameCalls => Volatile.Read(ref _nameCalls);

    private readonly ConcurrentDictionary<string, TimeSpan> _nameDelays = new(StringComparer.Ordinal);

    /// <summary>Makes every display-name answer for the tenant this slow (a slow Access).</summary>
    public void SetNameDelay(string tenantId, TimeSpan delay) => _nameDelays[tenantId] = delay;

    /// <summary>The display name (and email address, when given) the stub gives one user of one tenant (users without one are 404).</summary>
    public void SetDisplayName(string tenantId, string userId, string displayName, string? email = null) =>
        _displayNames[(tenantId, userId)] = (displayName, email);

    /// <summary>The name the stub gives the tenant (<c>tenantName</c> of its user lookups); a named tenant knows every user ID.</summary>
    public void SetTenantName(string tenantId, string tenantName) => _tenantNames[tenantId] = tenantName;

    private readonly ConcurrentDictionary<(string Tenant, string UserId), StubExplainedUser> _explained = new();

    /// <summary>
    /// A stored user of one tenant for <c>internal/explain</c> (users without one are 404): their roles (the permission sources come
    /// from the tenant's matrix), cost centers, headed departments, approval limit and whether they approve for finance.
    /// </summary>
    public void SetExplainedUser(
        string tenantId, string userId, string displayName, string[] roles, decimal approvalLimit,
        string[]? costCenters = null, string[]? departments = null, bool isFinanceApprover = false) =>
        _explained[(tenantId, userId)] = new StubExplainedUser(
            displayName, roles, costCenters ?? [], departments ?? [], approvalLimit, isFinanceApprover);

    private sealed record StubExplainedUser(
        string DisplayName, string[] Roles, string[] CostCenters, string[] Departments, decimal ApprovalLimit, bool IsFinanceApprover);

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
            var costCenterCodes = stub._costCenters.GetValueOrDefault((tenant, userId)) ?? ["CC-100"];
            return Results.Json(new { permissions, costCenterCodes, approvalLimit = 50000m });
        });
        app.MapPost("/api/v1/access/internal/registrations", async (HttpContext http, StubRegistrationBody body) =>
        {
            stub.Record(http);
            var registration = new StubRegistration(
                body.UserId, Header(http, "X-Tenant-Id"), Header(http, "X-On-Behalf-Of-User"),
                Header(http, "Idempotency-Key"));
            stub._registrations.Enqueue(registration);
            if (stub.Down)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                await (stub.OnRegister?.Invoke(registration) ?? Task.CompletedTask);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Results.Json(new { tenantId = registration.Tenant, joined = true });
        });
        app.MapGet("/api/v1/access/internal/users/{userId}", async (HttpContext http, string userId) =>
        {
            Interlocked.Increment(ref stub._nameCalls);
            stub.Record(http);
            if (stub._nameDelays.TryGetValue(http.Request.Headers["X-Tenant-Id"].ToString(), out var delay))
            {
                // Not the request's token: the stub keeps "working" after the caller gave up, as a slow server does.
                await Task.Delay(delay);
            }

            if (stub.Down)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // Without an address the stub leaves "email" out, as an Access from before plan 04 did.
            var tenant = http.Request.Headers["X-Tenant-Id"].ToString();
            var tenantName = stub._tenantNames.GetValueOrDefault(tenant);
            if (!stub._displayNames.TryGetValue((tenant, userId), out var user))
            {
                // A tenant with a name knows every user (the test does not know the user IDs of its login), under their ID.
                return tenantName is null ? Results.NotFound() : Results.Json(new { userId, displayName = userId, tenantName });
            }

            return user.Email is null
                ? Results.Json(new { userId, displayName = user.Name, tenantName })
                : Results.Json(new { userId, displayName = user.Name, email = user.Email, tenantName });
        });
        app.MapGet("/api/v1/access/internal/explain", (HttpContext http, string userId) =>
        {
            stub.Record(http);
            if (stub.Down)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var tenant = http.Request.Headers["X-Tenant-Id"].ToString();
            if (!stub._explained.TryGetValue((tenant, userId), out var user))
            {
                return Results.NotFound();
            }

            var matrix = stub._matrices.GetValueOrDefault(tenant) ?? DefaultMatrix;
            var sources = matrix.Where(r => user.Roles.Contains(r.Key, StringComparer.Ordinal))
                .SelectMany(r => r.Value.Select(permission => (Permission: permission, Role: r.Key)))
                .GroupBy(p => p.Permission, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Role).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            return Results.Json(new
            {
                userId,
                displayName = user.DisplayName,
                tenantId = tenant,
                roles = user.Roles.Order(StringComparer.Ordinal).ToArray(),
                permissionSources = sources,
                costCenterCodes = user.CostCenters,
                departmentCodes = user.Departments,
                approvalLimit = user.ApprovalLimit,
                isFinanceApprover = user.IsFinanceApprover,
            });
        });
        app.MapGet("/api/v1/access/internal/cost-centers/{code}/approvers", (HttpContext http, string code) =>
        {
            Interlocked.Increment(ref stub._approverCalls);
            stub.Record(http);
            if (stub.Down
                || (stub._approversFailUntil.TryGetValue(http.Request.Headers["X-Tenant-Id"].ToString(), out var until)
                    && DateTimeOffset.UtcNow < until))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (stub.MissingCostCenters.ContainsKey(code))
            {
                return Results.NotFound();
            }

            if (stub._approvers.TryGetValue((http.Request.Headers["X-Tenant-Id"].ToString(), code), out var configured))
            {
                return Results.Json(configured);
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

    private sealed record StubRegistrationBody(string UserId);

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
