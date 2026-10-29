using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProcureAi.Testing;

/// <summary>A budget of the stub Budgets, in Rand.</summary>
public sealed record StubBudget(string CostCenterCode, string FiscalYear, decimal Amount, decimal Reserved, decimal Spent)
{
    public decimal Available => Amount - Reserved - Spent;
}

/// <summary>
/// A real Kestrel server playing the Budgets service's <c>GET api/v1/budgets?fiscalYear=</c> for the Assistant's tools, which call it
/// as the user. Budgets are kept per tenant (the caller's tenant, <see cref="StubCaller"/>); a test can make a user forbidden
/// (<see cref="SetForbidden"/>: 403, as for a user without <c>budgets.read</c>), a tenant's calls hang until the caller gives up
/// (<see cref="SetHanging"/>), and a call without a credential is 401. Every call is recorded with the headers it arrived with
/// (<see cref="Calls"/>), so a test can assert the relayed <c>Authorization</c>. <see cref="Settings"/> points a service at it.
/// </summary>
public sealed class StubBudgetsServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, StubBudget[]> _budgets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Tenant, string User), bool> _forbidden = new();
    private readonly ConcurrentDictionary<string, bool> _hanging = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<StubUserCall> _calls = new();

    private StubBudgetsServer(WebApplication app) => _app = app;

    public string Url { get; private set; } = "";

    public IReadOnlyList<StubUserCall> Calls => [.. _calls];

    /// <summary>Configuration that makes a service find Budgets here (Aspire service discovery's configuration source).</summary>
    public Dictionary<string, string?> Settings => new() { ["Services:budgets-api:http:0"] = Url };

    public void SetBudgets(string tenantId, params StubBudget[] budgets) => _budgets[tenantId] = budgets;

    /// <summary>The user of the tenant gets 403.</summary>
    public void SetForbidden(string tenantId, string userId) => _forbidden[(tenantId, userId)] = true;

    /// <summary>The tenant's calls never answer (until the caller cancels).</summary>
    public void SetHanging(string tenantId) => _hanging[tenantId] = true;

    /// <summary>The calls of one tenant.</summary>
    public IReadOnlyList<StubUserCall> CallsOf(string tenantId) => [.. _calls.Where(c => c.Tenant == tenantId)];

    public static async Task<StubBudgetsServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new StubBudgetsServer(app);

        app.MapGet("/api/v1/budgets", async (HttpContext http, string? fiscalYear) =>
        {
            var call = StubCaller.Read(http);
            stub._calls.Enqueue(call);
            if (!StubCaller.HasCredential(call))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            if (call.Tenant is not null && stub._hanging.ContainsKey(call.Tenant))
            {
                await Task.Delay(Timeout.Infinite, http.RequestAborted);
            }

            if (call.Tenant is not null && call.User is not null && stub._forbidden.ContainsKey((call.Tenant, call.User)))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var budgets = call.Tenant is not null && stub._budgets.TryGetValue(call.Tenant, out var own) ? own : [];
            return Results.Json(budgets
                .Where(b => fiscalYear is null || b.FiscalYear == fiscalYear)
                .Select(b => new
                {
                    costCenterCode = b.CostCenterCode,
                    fiscalYear = b.FiscalYear,
                    amount = b.Amount,
                    reserved = b.Reserved,
                    spent = b.Spent,
                    available = b.Available,
                    version = 1,
                    ownerName = (string?)null,
                })
                .ToArray());
        });

        await app.StartAsync();
        stub.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
