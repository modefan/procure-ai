using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Notifications.Application.Inbox;
using Shouldly;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// The bell (plan 04 ruling 8): events become items of the users they concern, pushed live as <c>NotificationAdded</c> and listed by
/// <c>GET api/v1/notifications</c>; marking read; the last 50 per user; strictly per tenant and user. Each test uses tenants of its own.
/// </summary>
[Collection(NotificationsCollection.Name)]
public sealed class BellTests(NotificationsApp app)
{
    private const string Path = "/api/v1/notifications";
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewTenant() => $"t-{Guid.NewGuid():N}"[..16];

    private static string Token(string tenant, string user) => TestAuth.CreateToken(user, tenant, "requester");

    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        return request;
    }

    private async Task<BellListJson> ListAsync(string tenant, string user, NotificationsApiFactory? instance = null)
    {
        using var client = (instance ?? app.A).CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Get, Path, Token(tenant, user)), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BellListJson>(Ct))!;
    }

    private async Task<HttpStatusCode> PostAsync(string path, string tenant, string user)
    {
        using var client = app.B.CreateClient();
        using var response = await client.SendAsync(Request(HttpMethod.Post, path, Token(tenant, user)), Ct);
        return response.StatusCode;
    }

    private async Task<BellListJson> WaitForBellAsync(string tenant, string user, Func<BellListJson, bool> done)
    {
        var deadline = DateTime.UtcNow + Arrival;
        while (true)
        {
            var bell = await ListAsync(tenant, user);
            if (done(bell))
            {
                return bell;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The bell of {user} did not get there: {JsonSerializer.Serialize(bell)}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }

    private async Task AddItemsAsync(string tenant, string user, int count, DateTimeOffset start)
    {
        var store = app.A.Services.GetRequiredService<IBellStore>();
        for (var i = 0; i < count; i++)
        {
            (await store.AddAsync(tenant, user, new BellItem($"item-{i:D3}", $"Item {i}", null, "/requests", start.AddMinutes(i), false), Ct))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Requester_gets_bell_on_approved_and_rejected()
    {
        var tenant = NewTenant();
        var approvedId = Guid.NewGuid();
        var rejectedId = Guid.NewGuid();
        await using var tom = await LiveClient.ConnectAsync(app.B.Server, tenant, Token(tenant, "tom"));

        await app.PublishAsync(new RequestApproved(approvedId, tenant, "PR-1043", 100m, ["maria"], DateTimeOffset.UtcNow, "tom"));
        await app.PublishAsync(new RequestRejected(rejectedId, tenant, "PR-1044", "Too expensive", "maria", DateTimeOffset.UtcNow.AddSeconds(1), "tom"));

        var bell = await WaitForBellAsync(tenant, "tom", b => b.Items.Count == 2);
        bell.Unread.ShouldBe(2);
        bell.Items.Select(i => i.Text).ShouldBe(["Your request PR-1044 was rejected: Too expensive", "Your request PR-1043 was approved"]);
        bell.Items.Select(i => i.RequestId).ShouldBe([rejectedId, approvedId]);
        bell.Items[0].Link.ShouldBe($"/requests/{rejectedId}");
        bell.Items.ShouldAllBe(i => !i.Read);

        // Also pushed live to Tom's connection (on the other instance than the one that may have handled the event).
        var pushed = await tom.WaitForPushAsync(
            p => p.Method == LiveHubContract.NotificationAddedMethod && p.Payload.GetProperty("requestId").GetString() == approvedId.ToString(),
            Arrival);
        pushed.Payload.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "text", "requestId", "link", "createdAt", "read"], ignoreOrder: true);
        // The approver gets nothing for the requester's outcome.
        (await ListAsync(tenant, "maria")).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bell_keeps_last_50()
    {
        var tenant = NewTenant();
        await AddItemsAsync(tenant, "tom", 55, DateTimeOffset.UtcNow.AddHours(-2));

        var bell = await ListAsync(tenant, "tom");

        bell.Items.Count.ShouldBe(50);
        bell.Unread.ShouldBe(50);
        bell.Items[0].Id.ShouldBe("item-054", "newest first");
        bell.Items[^1].Id.ShouldBe("item-005", "the five oldest were dropped");
    }

    [Fact]
    public async Task Read_all_sets_unread_0()
    {
        var tenant = NewTenant();
        await AddItemsAsync(tenant, "tom", 3, DateTimeOffset.UtcNow.AddMinutes(-10));
        (await ListAsync(tenant, "tom")).Unread.ShouldBe(3); // cached for a second now

        (await PostAsync($"{Path}/item-001/read", tenant, "tom")).ShouldBe(HttpStatusCode.NoContent);
        var afterOne = await ListAsync(tenant, "tom");
        afterOne.Unread.ShouldBe(2, "the write evicted the cached read");
        afterOne.Items.Single(i => i.Id == "item-001").Read.ShouldBeTrue();

        (await PostAsync($"{Path}/read-all", tenant, "tom")).ShouldBe(HttpStatusCode.NoContent);
        var afterAll = await ListAsync(tenant, "tom", app.B);
        afterAll.Unread.ShouldBe(0);
        afterAll.Items.ShouldAllBe(i => i.Read);
        (await ListAsync(tenant, "tom")).Unread.ShouldBe(0, "on the other instance too");
    }

    [Fact]
    public async Task Bell_is_per_tenant_and_user()
    {
        var acme = NewTenant();
        var globex = NewTenant();
        await AddItemsAsync(acme, "tom", 2, DateTimeOffset.UtcNow.AddMinutes(-10));

        (await ListAsync(acme, "ben")).Items.ShouldBeEmpty("another user of the tenant");
        (await ListAsync(globex, "tom")).Items.ShouldBeEmpty("the same user ID in another tenant");
        (await PostAsync($"{Path}/item-000/read", acme, "ben")).ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync($"{Path}/item-000/read", globex, "tom")).ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync($"{Path}/read-all", globex, "tom")).ShouldBe(HttpStatusCode.NoContent);

        var own = await ListAsync(acme, "tom");
        own.Unread.ShouldBe(2, "nobody else could mark Tom's items read");
    }

    [Fact]
    public async Task Bell_needs_a_user_with_a_tenant()
    {
        using var client = app.A.CreateClient();
        using var anonymous = await client.GetAsync(Path, Ct);
        using var noTenant = await client.SendAsync(Request(HttpMethod.Get, Path, TestAuth.CreateToken("tom", tenant: null)), Ct);
        using var noIdempotencyKey = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{Path}/read-all") { Headers = { Authorization = new("Bearer", Token(NewTenant(), "tom")) } }, Ct);

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        noTenant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        noIdempotencyKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private sealed record BellListJson(List<BellItemJson> Items, int Unread);

    private sealed record BellItemJson(string Id, string Text, Guid? RequestId, string Link, DateTimeOffset CreatedAt, bool Read);
}
