using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProcureAi.Testing;

/// <summary>A request of the stub Requests (the fields the Assistant's <c>get_request_status</c> reads). <see cref="Status"/> is Requests' status name.</summary>
public sealed record StubRequest(Guid Id, string Number, string Title, string Status, string CostCenterCode, decimal TotalInclVat);

/// <summary>
/// A real Kestrel server playing the Requests service's <c>GET api/v1/requests/by-number/{number}</c> for the Assistant's tools, which
/// call it as the user. Requests are kept per tenant (the caller's tenant, <see cref="StubCaller"/>), so another tenant's number is 404
/// as in the real service; a call without a credential is 401. Every call is recorded with the headers it arrived with
/// (<see cref="Calls"/>), so a test can assert the relayed <c>Authorization</c>. <see cref="Settings"/> points a service at it.
/// </summary>
public sealed class StubRequestsServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<(string Tenant, string Number), StubRequest> _requests = new();
    private readonly ConcurrentQueue<StubUserCall> _calls = new();

    private StubRequestsServer(WebApplication app) => _app = app;

    public string Url { get; private set; } = "";

    public IReadOnlyList<StubUserCall> Calls => [.. _calls];

    /// <summary>Configuration that makes a service find Requests here (Aspire service discovery's configuration source).</summary>
    public Dictionary<string, string?> Settings => new() { ["Services:requests-api:http:0"] = Url };

    /// <summary>The tenant has <paramref name="request"/> (found by its number).</summary>
    public void SetRequest(string tenantId, StubRequest request) => _requests[(tenantId, request.Number)] = request;

    /// <summary>The calls of one tenant.</summary>
    public IReadOnlyList<StubUserCall> CallsOf(string tenantId) => [.. _calls.Where(c => c.Tenant == tenantId)];

    public static async Task<StubRequestsServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new StubRequestsServer(app);

        app.MapGet("/api/v1/requests/by-number/{number}", (HttpContext http, string number) =>
        {
            var call = StubCaller.Read(http);
            stub._calls.Enqueue(call);
            if (!StubCaller.HasCredential(call))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            return call.Tenant is not null && stub._requests.TryGetValue((call.Tenant, number), out var request)
                ? Results.Json(new
                {
                    id = request.Id,
                    number = request.Number,
                    title = request.Title,
                    status = request.Status,
                    costCenterCode = request.CostCenterCode,
                    totalInclVat = request.TotalInclVat,
                })
                : Results.NotFound();
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
