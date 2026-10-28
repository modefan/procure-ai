using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Contracts.Invoicing;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Contracts.Requests;
using ProcureAi.Notifications.Application.Live;
using Shouldly;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// The live hub end to end: an event published by a service to its exchange reaches the clients of its tenant (and nobody else) as
/// <c>EntityChanged</c>, also across instances through the Redis backplane. Each test uses tenants of its own.
/// </summary>
[Collection(NotificationsCollection.Name)]
public sealed class LiveHubTests(NotificationsApp app)
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(20);

    private static string NewTenant() => $"t-{Guid.NewGuid():N}"[..16];

    private static string UserToken(string tenant, string user = "tom") => TestAuth.CreateToken(user, tenant, "requester");

    private static RequestSubmitted Submitted(string tenant, Guid requestId) =>
        new(requestId, tenant, "PR-1", "Laptops", "tom", "CC-100", 100m, DateTimeOffset.UtcNow, "sup-1", []);

    private static bool IsRequest(JsonElement push, Guid requestId) =>
        push.GetProperty("entity").GetString() == LiveEntities.Request && push.GetProperty("id").GetString() == requestId.ToString();

    [Fact]
    public async Task Event_published_reaches_client_of_same_tenant()
    {
        var tenant = NewTenant();
        var requestId = Guid.NewGuid();
        await using var client = await LiveClient.ConnectAsync(app.A.Server, tenant, UserToken(tenant));

        await app.PublishAsync(Submitted(tenant, requestId));

        var push = await client.WaitForAsync(p => IsRequest(p, requestId), Arrival);
        // The wire format the Angular client reads: camelCase, IDs only.
        push.EnumerateObject().Select(p => p.Name).ShouldBe(["entity", "id", "requestId", "eventType", "occurredAt"], ignoreOrder: true);
        push.GetProperty("requestId").GetString().ShouldBe(requestId.ToString());
        push.GetProperty("eventType").GetString().ShouldBe(nameof(RequestSubmitted));
    }

    [Fact]
    public async Task One_event_becomes_one_push_per_entity()
    {
        var tenant = NewTenant();
        var requestId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        await using var client = await LiveClient.ConnectAsync(app.A.Server, tenant, UserToken(tenant));

        await app.PublishAsync(new InvoiceMatched(invoiceId, orderId, requestId, tenant, DateTimeOffset.UtcNow));

        await client.WaitForAsync(p => IsRequest(p, requestId), Arrival);
        client.Received.Select(p => (p.GetProperty("entity").GetString(), p.GetProperty("id").GetString())).ShouldBe(
        [
            (LiveEntities.Invoice, invoiceId.ToString()),
            (LiveEntities.Order, orderId.ToString()),
            (LiveEntities.Request, requestId.ToString()),
        ]);
    }

    [Fact]
    public async Task Other_tenant_client_receives_nothing()
    {
        var acme = NewTenant();
        var globex = NewTenant();
        var requestId = Guid.NewGuid();
        await using var acmeClient = await LiveClient.ConnectAsync(app.A.Server, acme, UserToken(acme));
        // Connected to both instances, so neither the instance that handles the event nor the backplane leaks it.
        await using var globexOnA = await LiveClient.ConnectAsync(app.A.Server, globex, UserToken(globex, "ben"));
        await using var globexOnB = await LiveClient.ConnectAsync(app.B.Server, globex, UserToken(globex, "ben"));

        await app.PublishAsync(Submitted(acme, requestId));

        await acmeClient.WaitForAsync(p => IsRequest(p, requestId), Arrival);
        // The push reached acme; give a stray one time to arrive before concluding nothing came.
        await Task.Delay(TimeSpan.FromSeconds(2));
        globexOnA.Received.ShouldBeEmpty();
        globexOnB.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_from_instance_A_reaches_client_on_instance_B()
    {
        var tenant = NewTenant();
        var requestId = Guid.NewGuid();
        await using var onB = await LiveClient.ConnectAsync(app.B.Server, tenant, UserToken(tenant));
        var change = new EntityChanged(LiveEntities.Request, requestId.ToString(), requestId, nameof(RequestSubmitted), DateTimeOffset.UtcNow);

        // Instance A pushes (as when A's listener took the message); the client is connected to B only.
        await app.A.Services.GetRequiredService<ILivePushes>().PushToTenantAsync(tenant, [change], CancellationToken.None);

        await onB.WaitForAsync(p => IsRequest(p, requestId), Arrival);
    }

    [Fact]
    public async Task Unauthenticated_connect_is_refused()
    {
        await using var client = LiveClient.For(app.A.Server);

        var refused = await Should.ThrowAsync<HttpRequestException>(() => client.Connection.StartAsync());

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_without_a_tenant_is_refused()
    {
        await using var client = LiveClient.For(app.A.Server, TestAuth.CreateToken("new-user", tenant: null));

        var refused = await Should.ThrowAsync<HttpRequestException>(() => client.Connection.StartAsync());

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Tenant_header_is_ignored_for_a_user_token()
    {
        var own = NewTenant();
        var other = NewTenant();
        var requestId = Guid.NewGuid();
        // A user cannot pick a tenant: X-Tenant-Id counts only with the admin key.
        await using var client = await LiveClient.ConnectAsync(
            app.A.Server, own, UserToken(own), new Dictionary<string, string> { ["X-Tenant-Id"] = other });

        await app.PublishAsync(Submitted(other, requestId));
        await app.PublishAsync(Submitted(own, requestId));

        await client.WaitForAsync(p => IsRequest(p, requestId), Arrival);
        await Task.Delay(TimeSpan.FromSeconds(1));
        client.Received.Count.ShouldBe(1, "only the push of the token's own tenant");
    }

    [Fact]
    public async Task Admin_key_with_tenant_header_connects()
    {
        var tenant = NewTenant();
        var requestId = Guid.NewGuid();
        await using var client = await LiveClient.ConnectAsync(
            app.A.Server, tenant, headers: new Dictionary<string, string> { ["X-Api-Key"] = TestAuth.ApiKey, ["X-Tenant-Id"] = tenant });

        await app.PublishAsync(Submitted(tenant, requestId));

        await client.WaitForAsync(p => IsRequest(p, requestId), Arrival);
    }

    [Fact]
    public async Task Admin_key_without_tenant_header_is_refused()
    {
        await using var client = LiveClient.For(app.A.Server, headers: new Dictionary<string, string> { ["X-Api-Key"] = TestAuth.ApiKey });

        var refused = await Should.ThrowAsync<HttpRequestException>(() => client.Connection.StartAsync());

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
