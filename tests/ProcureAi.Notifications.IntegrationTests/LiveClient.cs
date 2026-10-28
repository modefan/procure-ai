using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Notifications.Application.Live;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// A SignalR client of one Notifications instance, as the Angular app is one (long polling here: the in-memory test server has no
/// sockets). Keeps every <c>EntityChanged</c> it receives as the raw JSON, so the tests also see the wire format.
/// </summary>
public sealed class LiveClient : IAsyncDisposable
{
    private readonly ConcurrentQueue<JsonElement> _received = new();
    private readonly SemaphoreSlim _arrived = new(0);

    private LiveClient(HubConnection connection)
    {
        Connection = connection;
        connection.On<JsonElement>(LiveHubContract.EntityChangedMethod, message =>
        {
            _received.Enqueue(message.Clone());
            _arrived.Release();
        });
    }

    public HubConnection Connection { get; }

    /// <summary>The pushes received, without the probes of <see cref="ConnectAsync"/>.</summary>
    public IReadOnlyList<JsonElement> Received =>
        [.. _received.Where(r => !(r.GetProperty("id").GetString() ?? "").StartsWith("probe-", StringComparison.Ordinal))];

    /// <summary>A connection that is not started yet. <paramref name="token"/> as the bearer; <paramref name="headers"/> (admin key, tenant) as given.</summary>
    public static LiveClient For(TestServer server, string? token = null, Dictionary<string, string>? headers = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, LiveHubContract.Path), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }

                foreach (var (name, value) in headers ?? [])
                {
                    options.Headers[name] = value;
                }
            })
            .Build();
        return new LiveClient(connection);
    }

    /// <summary>
    /// Connects and waits until the connection is in the group of <paramref name="tenant"/>. The client's start completes with the
    /// handshake, before the hub's <c>OnConnectedAsync</c> has joined the groups, so a push sent at once could miss it (the Angular
    /// client revalidates after every connect for the same reason). The probe pushes are not kept.
    /// </summary>
    public static async Task<LiveClient> ConnectAsync(
        TestServer server, string tenant, string? token = null, Dictionary<string, string>? headers = null)
    {
        var client = For(server, token, headers);
        await client.Connection.StartAsync();
        await client.WaitUntilInTenantGroupAsync(server, tenant);
        return client;
    }

    private async Task WaitUntilInTenantGroupAsync(TestServer server, string tenant)
    {
        var pushes = server.Services.GetRequiredService<ILivePushes>();
        var probe = $"probe-{Guid.NewGuid():N}";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!_received.Any(IsProbe))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The connection did not join the group of tenant {tenant}.");
            }

            await pushes.PushToTenantAsync(
                tenant, [new EntityChanged(LiveEntities.Request, probe, null, "Probe", DateTimeOffset.UtcNow)], CancellationToken.None);
            await _arrived.WaitAsync(TimeSpan.FromMilliseconds(250));
        }

        // Further probe pushes may still arrive; Received leaves them out.
        bool IsProbe(JsonElement push) => push.GetProperty("id").GetString() == probe;
    }

    /// <summary>Waits for a push that matches; fails with what did arrive.</summary>
    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var found = Received.FirstOrDefault(match);
            if (found.ValueKind != JsonValueKind.Undefined)
            {
                return found;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"No matching EntityChanged within {timeout}. Received: {string.Join(" | ", _received.Select(r => r.GetRawText()))}");
            }

            await _arrived.WaitAsync(left);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        _arrived.Dispose();
    }
}
