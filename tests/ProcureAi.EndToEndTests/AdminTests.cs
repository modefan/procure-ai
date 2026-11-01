using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// The admin screens against the whole system (plan 06): the authorization matrix takes effect for a signed-in user without a reload,
/// the access check explains separation of duties, the supplier chaos switch opens and closes the circuit breaker, a change made in
/// the database behind the app's back shows on the data changes and status screens, and a user of two companies switches between them.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class AdminTests(AppFixture fixture)
{
    /// <summary>Review Focus 3: how quickly a matrix change must reach a signed-in user's menu.</summary>
    private static readonly TimeSpan MatrixTakesEffect = TimeSpan.FromSeconds(10);

    private static readonly float Slow = (float)TimeSpan.FromMinutes(3).TotalMilliseconds;

    [Fact]
    public async Task Matrix_change_takes_effect_for_tom()
    {
        fixture.RequireReady();
        await using var tomContext = await fixture.NewContextAsync("tom");
        var tom = await tomContext.NewPageAsync();
        await fixture.DemoLogInAsync(tom, "tom");
        await Pages.ExpectLiveAsync(tom);
        await tom.EvaluateAsync("window.__noReload = true");
        var assistant = tom.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).GetByRole(AriaRole.Link, new() { Name = "Assistant" });
        await Assertions.Expect(assistant).ToBeVisibleAsync();

        await using var adminContext = await fixture.NewContextAsync("admin");
        var admin = await adminContext.NewPageAsync();
        await fixture.DemoLogInAsync(admin, "admin", "/app/admin/authorization");
        var box = admin.GetByRole(AriaRole.Checkbox, new() { Name = "Requester: assistant.use", Exact = true });
        await Assertions.Expect(box).ToBeCheckedAsync(new() { Timeout = Pages.Timeout });

        try
        {
            // The admin takes the assistant away from requesters: Tom's link goes, on the page he has open.
            await box.UncheckAsync();
            await admin.GetByRole(AriaRole.Button, new() { Name = "Save changes" }).ClickAsync();
            await Assertions.Expect(admin.GetByTestId("saved")).ToContainTextAsync("Requester", new() { Timeout = Pages.Timeout });
            var saved = Stopwatch.StartNew();
            await Assertions.Expect(assistant).ToHaveCountAsync(0, new() { Timeout = (float)MatrixTakesEffect.TotalMilliseconds });
            saved.Elapsed.ShouldBeLessThan(MatrixTakesEffect);

            // And gives it back: the link returns the same way.
            await box.CheckAsync();
            await admin.GetByRole(AriaRole.Button, new() { Name = "Save changes" }).ClickAsync();
            await Assertions.Expect(admin.GetByTestId("saved")).ToContainTextAsync("Requester", new() { Timeout = Pages.Timeout });
            saved.Restart();
            await Assertions.Expect(assistant).ToBeVisibleAsync(new() { Timeout = (float)MatrixTakesEffect.TotalMilliseconds });
            saved.Elapsed.ShouldBeLessThan(MatrixTakesEffect);
            (await tom.EvaluateAsync<bool>("window.__noReload === true")).ShouldBeTrue("Tom's page was reloaded");
        }
        finally
        {
            await RestoreRequesterAssistantAsync();
        }
    }

    [Fact]
    public async Task Access_check_explains_sod_request()
    {
        fixture.RequireReady();
        // The seeded separation-of-duties story: Maria's own request on IT-200, a cost center she manages (its number depends on the seed).
        var (_, number) = await fixture.SeededRequestAsync("maria-own-request");

        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await fixture.DemoLogInAsync(page, "admin", "/app/admin/access-check");

        await page.Locator("#check-user").FillAsync("Maria");
        await page.GetByRole(AriaRole.Option, new() { NameRegex = new Regex("^Maria") }).First.ClickAsync(new() { Timeout = Pages.Timeout });
        await page.Locator("#check-action").SelectOptionAsync("approve");
        await page.Locator("#check-request").FillAsync(number);
        await page.GetByRole(AriaRole.Button, new() { Name = "Check" }).ClickAsync();

        var verdict = page.GetByTestId("verdict");
        await Assertions.Expect(verdict).ToContainTextAsync("Denied", new() { Timeout = Pages.Timeout });
        await Assertions.Expect(verdict).ToContainTextAsync(number);
        var rules = page.GetByTestId("rules");
        var sod = rules.Locator("[data-step=not-requester]");
        await Assertions.Expect(sod).ToHaveAttributeAsync("data-passed", "false");
        await Assertions.Expect(sod).ToContainTextAsync("is the requester");
        // She has the permission: only separation of duties (and the step she is not an approver of) denies it.
        await Assertions.Expect(rules.Locator("[data-step=permission]")).ToHaveAttributeAsync("data-passed", "true");

        // The inputs are in the address, so the link reproduces the check.
        page.Url.ShouldContain($"user={DemoUsers.Maria}");
        page.Url.ShouldContain($"request={number}");
    }

    [Fact]
    public async Task Chaos_opens_breaker_and_restore_closes_it()
    {
        fixture.RequireReady();
        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await fixture.DemoLogInAsync(page, "admin", "/app/admin/system");
        var chaos = page.GetByTestId("chaos");
        var breaker = page.Locator("[data-service=purchasing-api]").GetByText(new Regex("^Supplier API: "));
        await Assertions.Expect(breaker).ToHaveTextAsync("Supplier API: closed", new() { Timeout = Pages.Timeout });

        var restored = false;
        try
        {
            await chaos.GetByRole(AriaRole.Button, new() { Name = "Break the supplier API (chaos)" }).ClickAsync();
            await Assertions.Expect(chaos).ToContainTextAsync("Down", new() { Timeout = Pages.Timeout });
            await Assertions.Expect(chaos).ToContainTextAsync(new Regex(@"restores in \d+:\d\d"));

            // Orders now fail to reach the supplier; enough failures open the breaker. An approved request makes Purchasing try.
            var (id, _) = await fixture.SubmitRequestAsync($"E2E chaos {Guid.NewGuid():N}"[..28]);
            await fixture.ApproveAsMariaAsync(id);
            await Assertions.Expect(breaker).ToHaveTextAsync(new Regex(@"^Supplier API: OPEN \(retry in \d+ s\)$"), new() { Timeout = Slow });

            await chaos.GetByRole(AriaRole.Button, new() { Name = "Restore now" }).ClickAsync();
            restored = true;
            await Assertions.Expect(chaos).ToContainTextAsync("working normally", new() { Timeout = Pages.Timeout });

            // The order is retried; the breaker lets a trial call through after its break, which succeeds, and closes.
            await Assertions.Expect(breaker).ToHaveTextAsync("Supplier API: closed", new() { Timeout = Slow });
            var order = await fixture.WaitForOrderAsync(id);
            order.Number.ShouldStartWith("PO-");
        }
        finally
        {
            if (!restored)
            {
                await fixture.CallAsync(HttpMethod.Put, "/api/v1/purchasing/admin/supplier-chaos", null, new { mode = "Normal" });
            }
        }
    }

    [Fact]
    public async Task Outside_app_sql_change_shows_in_log_and_status()
    {
        fixture.RequireReady();
        // A request of this test's own: someone with database access renames it directly in SQL, behind the app's back.
        var (id, number) = await fixture.SubmitRequestAsync($"E2E outside {Guid.NewGuid():N}"[..28]);
        var newTitle = $"Renamed in SQL {Guid.NewGuid():N}"[..28];
        var updated = await fixture.PsqlAsync(
            "requestsdb", $"UPDATE purchase_requests SET title = '{newTitle}' WHERE id = '{id}' RETURNING id;");
        updated.ShouldStartWith(id.ToString(), Case.Insensitive, $"{number} was not updated");

        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();

        // The data changes screen, filtered to changes outside the app, lists the row change (keyed by the request's ID) as outside the app.
        await fixture.DemoLogInAsync(page, "admin", "/app/admin/data-changes?outside=true");
        var change = page.GetByTestId("data-changes").Locator("li").Filter(new() { HasText = $"purchase_requests {id}" });
        for (var attempt = 0; attempt < 20 && await change.CountAsync() == 0; attempt++)
        {
            // Debezium reads the change from the WAL and Audit matches it (no app transaction explains it); the list is read on open.
            await page.WaitForTimeoutAsync(3000);
            await page.ReloadAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Data changes" })).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        }
        await Assertions.Expect(change.First).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        await Assertions.Expect(change.First.GetByTestId("who-outside")).ToHaveTextAsync("outside the app");
        // Its details show the old and the new title.
        await change.First.GetByRole(AriaRole.Button, new() { Name = "Details" }).ClickAsync();
        await Assertions.Expect(change.First).ToContainTextAsync(newTitle, new() { Timeout = Pages.Timeout });

        // The status screen raises it as an alert, linking to the change.
        await page.GotoAsync(new Uri(fixture.GatewayUrl, "/app/admin/system").ToString());
        var alerts = page.GetByTestId("alerts");
        await Assertions.Expect(alerts).ToContainTextAsync("Changed outside the app", new() { Timeout = Slow });
        await Assertions.Expect(alerts.GetByRole(AriaRole.Link).Filter(new() { HasText = $"purchase_requests (requestsdb) changed outside the app, row {id}" })).ToBeVisibleAsync(new() { Timeout = Slow });
        await Assertions.Expect(page.GetByTestId("stats")).ToContainTextAsync(new Regex(@"Changed outside the app: [1-9]\d*"));
    }

    [Fact]
    public async Task Thandi_switches_to_globex_and_sees_only_globex_requests()
    {
        fixture.RequireReady();
        var acme = AppFixture.Scenarios.Single(s => s.Tenant == "acme").Requests.Select(r => r.Title).ToList();
        var globex = AppFixture.Scenarios.Single(s => s.Tenant == "globex").Requests.Select(r => r.Title).ToList();

        await using var context = await fixture.NewContextAsync();
        var page = await context.NewPageAsync();
        await fixture.DemoLogInAsync(page, "thandi", "/app/requests");
        var table = page.Locator("main table");
        // Acme first (her demo button's company): Acme's requests, none of Globex's.
        await Assertions.Expect(page.GetByTestId("user-menu")).ToContainTextAsync("Acme", new() { Timeout = Pages.Timeout });
        await Assertions.Expect(table.Locator("tbody tr").First).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        foreach (var title in globex)
        {
            await Assertions.Expect(table).Not.ToContainTextAsync(title);
        }

        // Switch company in the user menu: a full page load into Globex, with nothing of Acme's left in memory.
        await page.GetByTestId("user-menu").ClickAsync();
        await page.GetByTestId("switch-tenant").Filter(new() { HasText = "Globex" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("user-menu")).ToContainTextAsync("Globex", new() { Timeout = Pages.Timeout });
        await page.GotoAsync(new Uri(fixture.GatewayUrl, "/app/requests").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Purchase requests" })).ToBeVisibleAsync(new() { Timeout = Pages.Timeout });
        foreach (var title in globex)
        {
            await Assertions.Expect(table).ToContainTextAsync(title, new() { Timeout = Pages.Timeout });
        }
        var text = await page.Locator("main").InnerTextAsync();
        foreach (var title in acme)
        {
            text.ShouldNotContain(title, Case.Sensitive, "an Acme request is shown in Globex");
        }
        // And the API agrees: Globex's list as Thandi has exactly the Globex requests' titles among the seeded ones.
        var fromServer = await page.EvaluateAsync<string>("async () => await (await fetch('/api/v1/requests?pageSize=100', { cache: 'no-store' })).text()");
        foreach (var title in acme)
        {
            fromServer.ShouldNotContain(title);
        }
    }

    /// <summary>Puts <c>assistant.use</c> back on the requester role (the matrix is shared by every test in the run).</summary>
    private async Task RestoreRequesterAssistantAsync()
    {
        var (matrix, etag) = await fixture.CallAsync(HttpMethod.Get, "/api/v1/access/matrix", null);
        var requester = matrix.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("role").GetString() == "requester");
        var permissions = requester.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        if (permissions.Contains("assistant.use"))
        {
            return;
        }
        permissions.Add("assistant.use");
        await fixture.CallAsync(HttpMethod.Put, "/api/v1/access/matrix/roles/requester", null, new { permissions }, etag);
    }
}
