using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using ProcureAi.Contracts.Notifications;
using Shouldly;

namespace ProcureAi.SystemTests;

/// <summary>
/// Notifications on the whole system (plan 04): the live pushes through the gateway's WebSocket route of <c>/hubs/live</c>, and the bell and
/// email of an approver, fed by the real events of the real services. Each test raises a small request of its own (R 4 830,00 incl. VAT,
/// one approval step) in tenant <c>acme</c>.
/// </summary>
[Collection(SystemCollection.Name)]
public sealed class LiveUpdateTests(SystemFixture system)
{
    private const string MariaEmail = "maria@acme.test";

    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(60);

    private readonly SystemFlow _flow = new(system);

    [Fact]
    public async Task Submitting_a_request_pushes_EntityChanged_through_the_gateway()
    {
        system.RequireReady();
        await using var live = await HubClient.ConnectAsync(system);

        var request = await _flow.SubmitAsync("Push test monitor", "MON-27", 1, 4_830.00m);

        // The request's own push and the approval chain's push (its ID is the request ID), then the budget of the cost center.
        var id = request.Id.ToString();
        var submitted = await live.WaitForAsync(p => p.Entity == "request" && p.Id == id && p.EventType == "RequestSubmitted");
        submitted.RequestId.ShouldBe(request.Id);
        await live.WaitForAsync(p => p.Entity == "approval" && p.Id == id && p.EventType == "ApprovalRequested");
        await live.WaitForAsync(p => p.Entity == "budget" && p.Id == SystemFlow.CostCenter && p.RequestId == request.Id);

        // IDs only: nothing but the five fields of the contract travels (no title, no amount).
        live.Raw.Where(r => r.GetProperty("id").GetString() == id).ShouldAllBe(
            r => r.EnumerateObject().Select(f => f.Name).Order().SequenceEqual(new[] { "entity", "eventType", "id", "occurredAt", "requestId" }));

        // The decision is pushed too.
        await _flow.ApproveAsync(SystemFixture.Maria, request.Id, expectedStep: 1, expectedLevel: "Manager", "R 4 830,00");
        await live.WaitForAsync(p => p.Entity == "request" && p.Id == id && p.EventType == "RequestApproved");
    }

    [Fact]
    public async Task Approver_gets_bell_item_and_one_email_in_mailpit()
    {
        system.RequireReady();
        using var mailpit = new MailpitClient(system);
        var title = $"Bell test {Guid.NewGuid():N}"[..20];
        var request = await _flow.SubmitAsync(title, "MON-27", 1, 4_830.00m);
        var subject = $"{request.Number} is waiting for your approval";

        // The bell: one unread item for Maria, about this request, with the link to it. Tom has none for it.
        using var mariaBell = system.Service("notifications", SystemFixture.Maria);
        var bell = await SystemFlow.EventuallyAsync(
            () => mariaBell.GetAsync<BellListView>("/api/v1/notifications"),
            r => r.Body.Items.Any(i => i.RequestId == request.Id),
            "bell item for Maria");
        var item = bell.Body.Items.Single(i => i.RequestId == request.Id);
        item.Text.ShouldBe(subject);
        item.Link.ShouldBe($"/requests/{request.Id}");
        item.Read.ShouldBeFalse();
        bell.Body.Unread.ShouldBeGreaterThan(0);
        using var tomBell = system.Service("notifications", SystemFixture.Tom);
        (await tomBell.GetAsync<BellListView>("/api/v1/notifications")).Body.Items.ShouldNotContain(i => i.RequestId == request.Id);

        // The email: exactly one to Maria, with the request's link. Only emails about this request count: a demo reset earlier in the run
        // restarts the request numbers, so an email of an older request can have the same subject (Mailpit keeps everything).
        var email = await mailpit.WaitForAsync(MariaEmail, subject, request.Id);
        (await mailpit.TextAsync(email)).ShouldContain($"/requests/{request.Id}");

        // Approving sends more events (the decision, the order); the approver is not emailed again for this request.
        await _flow.ApproveAsync(SystemFixture.Maria, request.Id, expectedStep: 1, expectedLevel: "Manager", "R 4 830,00");
        await _flow.RequestWhenAsync(request.Id, r => r.Status is "Approved" or "Ordered", "request approved");
        (await mailpit.MessagesToAsync(MariaEmail, subject, request.Id)).Count.ShouldBe(1);
    }

    /// <summary>A SignalR client of the gateway (WebSockets, the dev certificate) as the admin key in tenant acme; keeps every EntityChanged.</summary>
    private sealed class HubClient : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly ConcurrentQueue<JsonElement> _raw = new();
        private readonly SemaphoreSlim _arrived = new(0);

        private HubClient(SystemFixture system)
        {
            using var gateway = system.Gateway();
            _connection = new HubConnectionBuilder()
                .WithUrl(new Uri(gateway.BaseAddress!, LiveHubContract.Path), options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.HttpMessageHandlerFactory = _ => new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                    };
                    options.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                    options.Headers["X-Api-Key"] = system.AdminApiKey;
                    options.Headers["X-Tenant-Id"] = SystemFixture.Tenant;
                })
                .Build();
            _connection.On<JsonElement>(LiveHubContract.EntityChangedMethod, push =>
            {
                _raw.Enqueue(push.Clone());
                _arrived.Release();
            });
        }

        public IReadOnlyList<JsonElement> Raw => [.. _raw];

        public static async Task<HubClient> ConnectAsync(SystemFixture system)
        {
            var client = new HubClient(system);
            await client._connection.StartAsync(TestContext.Current.CancellationToken);
            // A hub method runs only after OnConnectedAsync has joined the groups, so once it has answered nothing is missed.
            await client._connection.InvokeAsync(LiveHubContract.HeartbeatMethod, TestContext.Current.CancellationToken);
            return client;
        }

        public async Task<EntityChanged> WaitForAsync(Func<EntityChanged, bool> match)
        {
            var deadline = DateTime.UtcNow + Arrival;
            while (true)
            {
                var found = _raw.Select(r => r.Deserialize<EntityChanged>(ServiceClient.Json)!).FirstOrDefault(match);
                if (found is not null)
                {
                    return found;
                }

                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"No matching push within {Arrival}. Received: {string.Join(" | ", _raw.Select(r => r.GetRawText()))}");
                }

                await _arrived.WaitAsync(left, TestContext.Current.CancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            _arrived.Dispose();
        }
    }

    /// <summary>Mailpit's HTTP API (the AppHost's mail catcher): what the Notifications service sent.</summary>
    private sealed class MailpitClient(SystemFixture system) : IDisposable
    {
        private readonly HttpClient _http = new() { BaseAddress = new Uri(system.App.GetEndpoint("mailpit", "http").ToString()) };

        /// <summary>The emails to <paramref name="address"/> with <paramref name="subject"/> whose text links to <paramref name="requestId"/>.</summary>
        public async Task<IReadOnlyList<MailView>> MessagesToAsync(string address, string subject, Guid requestId)
        {
            var page = await _http.GetFromJsonAsync<MailPage>("/api/v1/messages?limit=1000", TestContext.Current.CancellationToken);
            var candidates = page!.Messages.Where(m => m.Subject == subject && m.To.Any(t => string.Equals(t.Address, address, StringComparison.OrdinalIgnoreCase)));
            var about = new List<MailView>();
            foreach (var message in candidates)
            {
                if ((await TextAsync(message)).Contains($"/requests/{requestId}", StringComparison.Ordinal))
                {
                    about.Add(message);
                }
            }
            return about;
        }

        public async Task<MailView> WaitForAsync(string address, string subject, Guid requestId) =>
            (await SystemFlow.EventuallyAsync(() => MessagesToAsync(address, subject, requestId), m => m.Count > 0, $"email '{subject}' to {address}")).Single();

        public async Task<string> TextAsync(MailView message)
        {
            var detail = await _http.GetFromJsonAsync<MailDetail>($"/api/v1/message/{message.Id}", TestContext.Current.CancellationToken);
            return detail!.Text;
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed record MailAddress(string Address);

    private sealed record MailView(string Id, string Subject, IReadOnlyList<MailAddress> To);

    private sealed record MailPage(IReadOnlyList<MailView> Messages);

    private sealed record MailDetail(string Text);
}

public sealed record BellItemView(string Id, string Text, Guid? RequestId, string Link, DateTimeOffset CreatedAt, bool Read);

public sealed record BellListView(IReadOnlyList<BellItemView> Items, int Unread);
