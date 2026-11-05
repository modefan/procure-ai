using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// The live screens in a real browser against the whole system (plan 04): a request raised in the form and approved by someone else shows
/// up without a reload, presence, and the connection indicator through a restart of the Notifications service. Each test uses its own
/// requests, found by their title or number.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class FlowTests(AppFixture fixture)
{
    private static string NewTitle(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..28];

    [Fact]
    public async Task Tom_submits_and_sees_approval_live_without_reload()
    {
        fixture.RequireReady();
        var title = NewTitle("E2E live");

        await using var tomContext = await fixture.NewContextAsync();
        var tom = await tomContext.NewPageAsync();
        await Pages.LogInAsync(fixture, tom, AppFixture.DemoUser, "/app/requests/new");

        // Tom raises a request in the form: one monitor from Contoso, R 4 830,00 incl. VAT, so Maria alone approves it.
        // The title goes in first, while the reference data may still be loading: it must survive that (a bug this test found).
        await tom.Locator("#title").FillAsync(title);
        await tom.Locator("#supplier").SelectOptionAsync("contoso-hardware");
        await tom.Locator("#cost-center").SelectOptionAsync("IT-200");
        await tom.GetByLabel("Item, line 1").SelectOptionAsync("MON-27");
        await Assertions.Expect(tom.Locator("#title")).ToHaveValueAsync(title);
        await tom.GetByTestId("submit").ClickAsync();
        await tom.WaitForURLAsync(new Regex(@"/app/requests/[0-9a-f-]{36}$"), new() { Timeout = Pages.Timeout });
        var number = (await tom.Locator("main p.font-semibold").First.InnerTextAsync()).Split('·')[0].Trim();
        number.ShouldStartWith("PR-");
        await Assertions.Expect(Pages.RequestStatus(tom)).ToHaveTextAsync("Submitted");
        await Pages.ExpectLiveAsync(tom);

        // Whatever happens next must reach this very page: a marker that a reload would lose.
        await tom.EvaluateAsync("window.__noReload = true");

        // Maria, in her own browser, finds it in her inbox and approves it.
        await using var mariaContext = await fixture.NewContextAsync("maria");
        var maria = await mariaContext.NewPageAsync();
        await Pages.LogInAsync(fixture, maria, "maria@acme.test", "/app/approvals");
        await Pages.ApproveInInboxAsync(maria, number);

        // Tom's page moves on by itself: approved (then ordered), and the audit trail grows with Maria's decision.
        await Assertions.Expect(Pages.RequestStatus(tom)).ToHaveTextAsync(new Regex("^(Approved|Ordered|Completed)$"), new() { Timeout = Pages.Timeout });
        await Assertions.Expect(tom.GetByRole(AriaRole.Table, new() { Name = "Audit trail" })).ToContainTextAsync("Maria", new() { Timeout = Pages.Timeout });
        (await tom.EvaluateAsync<bool>("window.__noReload === true")).ShouldBeTrue("the page was reloaded");
    }

    /// <summary>
    /// Final review I2: Notifications and Requests read the approval event at the same moment, so the push can beat the status write.
    /// Tom's request list, left open in a second tab, must still end up with the new status (the trailing reload), without a reload.
    /// </summary>
    [Fact]
    public async Task Requests_list_in_another_tab_shows_the_new_status_after_an_approval()
    {
        fixture.RequireReady();
        var (_, number) = await fixture.SubmitRequestAsync(NewTitle("E2E list"));

        await using var tomContext = await fixture.NewContextAsync();
        var tom = await tomContext.NewPageAsync();
        await Pages.LogInAsync(fixture, tom, AppFixture.DemoUser, "/app/requests");
        var rowTom = tom.Locator("tr").Filter(new() { HasText = number });
        await Assertions.Expect(rowTom.Locator("app-status-badge")).ToHaveTextAsync("Submitted", new() { Timeout = Pages.Timeout });
        await Pages.ExpectLiveAsync(tom);
        await tom.EvaluateAsync("window.__noReload = true");

        await using var mariaContext = await fixture.NewContextAsync("maria");
        var maria = await mariaContext.NewPageAsync();
        await Pages.LogInAsync(fixture, maria, "maria@acme.test", "/app/approvals");
        await Pages.ApproveInInboxAsync(maria, number);

        await Assertions.Expect(rowTom.Locator("app-status-badge")).ToHaveTextAsync(new Regex("^(Approved|Ordered|Completed)$"), new() { Timeout = Pages.Timeout });
        (await tom.EvaluateAsync<bool>("window.__noReload === true")).ShouldBeTrue("the page was reloaded");
        await tom.ScreenshotAsync(new() { Path = Path.Combine(AppFixture.ArtifactsDirectory, "final-requests-list-live.png") });
    }

    [Fact]
    public async Task Maria_sees_Tom_viewing_the_request()
    {
        fixture.RequireReady();
        var (id, _) = await fixture.SubmitRequestAsync(NewTitle("E2E presence"));

        await using var mariaContext = await fixture.NewContextAsync("maria");
        var maria = await mariaContext.NewPageAsync();
        await Pages.LogInAsync(fixture, maria, "maria@acme.test", $"/app/requests/{id}");
        await Assertions.Expect(Pages.RequestStatus(maria)).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Pages.ExpectLiveAsync(maria);
        await Assertions.Expect(maria.GetByText("is also viewing")).ToHaveCountAsync(0);

        await using var tomContext = await fixture.NewContextAsync("tom");
        var tom = await tomContext.NewPageAsync();
        await Pages.LogInAsync(fixture, tom, AppFixture.DemoUser, $"/app/requests/{id}");

        // Maria sees who is looking: Tom, by his name. Tom does not see himself.
        var viewing = maria.Locator("main").GetByText(new Regex("Tom .* is also viewing"));
        await Assertions.Expect(viewing).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(tom.Locator("main").GetByText(new Regex("Maria .* is also viewing"))).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(tom.Locator("main").GetByText(new Regex("Tom .* is also viewing"))).ToHaveCountAsync(0);

        // Tom moves to another page: Maria no longer sees him there.
        await tom.GetByRole(AriaRole.Link, new() { Name = "All requests" }).ClickAsync();
        await Assertions.Expect(viewing).ToHaveCountAsync(0, new() { Timeout = Pages.Timeout });
    }

    /// <summary>
    /// Plan 07 Review Focus 5 (and the Task 5 carry-over): the live connection is a real WebSocket through the gateway (on the cluster also
    /// through Traefik), not SignalR's silent fallback to Server-Sent Events or long polling, and pushes arrive over it.
    /// </summary>
    [Fact]
    public async Task Live_connection_is_a_websocket_through_the_gateway()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        var sockets = new ConcurrentQueue<IWebSocket>();
        var framesReceived = 0;
        var fallbackRequests = new ConcurrentQueue<string>();
        page.WebSocket += (_, socket) =>
        {
            if (new Uri(socket.Url).AbsolutePath == "/hubs/live")
            {
                sockets.Enqueue(socket);
                socket.FrameReceived += (_, _) => Interlocked.Increment(ref framesReceived);
            }
        };
        page.Request += (_, request) =>
        {
            // negotiate (POST /hubs/live/negotiate) comes first with every transport; any other HTTP request to the hub is a fallback.
            var path = new Uri(request.Url).AbsolutePath;
            if (path.StartsWith("/hubs/live", StringComparison.Ordinal) && path != "/hubs/live/negotiate"
                && request.ResourceType is "xhr" or "fetch" or "eventsource")
            {
                fallbackRequests.Enqueue($"{request.Method} {request.Url} ({request.ResourceType})");
            }
        };

        await fixture.DemoLogInAsync(page, "tom", "/app/requests");
        await Pages.ExpectLiveAsync(page);

        // A request submitted by someone else appears in the list: the push came in, over the socket.
        var title = NewTitle("E2E websocket");
        await fixture.SubmitRequestAsync(title);
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = title })).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });

        fallbackRequests.ShouldBeEmpty("SignalR fell back from WebSockets");
        sockets.ShouldNotBeEmpty("no WebSocket to /hubs/live was opened");
        var socket = sockets.Last();
        socket.Url.ShouldStartWith($"wss://{fixture.GatewayUrl.Authority}/hubs/live");
        socket.IsClosed.ShouldBeFalse("the hub's WebSocket closed");
        // The handshake's answer and at least one message after it.
        Volatile.Read(ref framesReceived).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    [Trait("Target", "AppHost")]
    public async Task Restarting_notifications_shows_reconnecting_then_live()
    {
        fixture.RequireAppHost(
            "it stops and starts notifications-api with the AppHost's resource commands (on the cluster Argo CD's self-heal would undo a "
            + "scale to zero within seconds)");
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await Pages.LogInAsync(fixture, page, AppFixture.DemoUser, "/app/requests");
        await Pages.ExpectLiveAsync(page);
        await page.EvaluateAsync("window.__noReload = true");
        await Assertions.Expect(page.GetByTestId("live-banner")).ToHaveCountAsync(0);

        var stopped = false;
        try
        {
            await fixture.StopAsync("notifications-api");
            stopped = true;

            // The connection drops: the indicator and the banner say so (the app keeps working).
            await Assertions.Expect(Pages.LiveIndicator(page)).ToHaveTextAsync(new Regex("Reconnecting|Offline"), new() { Timeout = Pages.Timeout });
            await Assertions.Expect(page.GetByTestId("live-banner")).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Purchase requests" })).ToBeVisibleAsync();

            await fixture.StartAsync("notifications-api");
            stopped = false;
        }
        finally
        {
            if (stopped)
            {
                await fixture.StartAsync("notifications-api");
            }
        }

        // Back: the same page connects again by itself (SignalR retries, then the app retries every 30 s), without a reload.
        await Pages.ExpectLiveAsync(page, (float)TimeSpan.FromMinutes(3).TotalMilliseconds);
        await Assertions.Expect(page.GetByTestId("live-banner")).ToHaveCountAsync(0);
        (await page.EvaluateAsync<bool>("window.__noReload === true")).ShouldBeTrue("the page was reloaded");

        // And pushes flow again: a request submitted by someone else appears in the list.
        var title = NewTitle("E2E after restart");
        await fixture.SubmitRequestAsync(title);
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = title })).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
    }
}
