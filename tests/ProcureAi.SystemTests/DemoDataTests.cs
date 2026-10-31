using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Aspire.Hosting.Testing;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcureAi.Contracts;
using ProcureAi.DemoData;
using Shouldly;
using ChainView = ProcureAi.DemoData.ChainView;
using SeededRequest = ProcureAi.DemoData.RequestView;

namespace ProcureAi.SystemTests;

/// <summary>
/// The demo data (plan 06 Task 15): the AppHost's demo-data resource seeds acme and globex through the gateway once the gateway is healthy
/// (the fixture waits for it). Every scenario request reached its state, a second run creates nothing, and PO-1001 is Fabrikam's dock and
/// stand, received and not invoiced, for the invoice scan demo.
/// </summary>
[Collection(SystemCollection.Name)]
public sealed class DemoDataTests(SystemFixture system)
{
    private static readonly IReadOnlyList<Scenario> AllScenarios = Scenarios.Load(Path.Combine(AppContext.BaseDirectory, "Scenarios"));

    [Fact]
    public async Task Seed_produces_every_state_in_both_tenants()
    {
        system.RequireReady();
        system.SeedExitCode.ShouldBe(0, "the AppHost's demo-data seed failed. Its log:" + Environment.NewLine + system.SeedLog);

        foreach (var scenario in AllScenarios)
        {
            foreach (var expected in scenario.Requests)
            {
                var what = $"{scenario.Tenant}/{expected.Key} ({expected.StopAt})";
                var requester = DemoUsers.All[expected.Requester];
                var request = (await RequestsOfAsync(scenario.Tenant, requester)).SingleOrDefault(r => r.Title == expected.Title)
                    .ShouldNotBeNull($"{what} was not seeded");
                request.RequesterId.ShouldBe(requester, what);
                request.Status.ShouldBe(RequestStatus(expected.StopAt), what);
                await StateMatchesAsync(scenario, expected, request, what);
            }
        }
    }

    [Fact]
    public async Task Seed_twice_creates_nothing_new()
    {
        system.RequireReady();
        var before = await CountsAsync();

        foreach (var scenario in AllScenarios)
        {
            var result = await Runner().RunAsync(scenario, TestContext.Current.CancellationToken);
            result.Failures.Select(f => f.Message).ShouldBeEmpty();
            result.Created.ShouldBe(0, scenario.Tenant);
            result.Found.ShouldBe(scenario.Requests.Count, scenario.Tenant);
        }

        (await CountsAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Po_1001_is_received_and_not_invoiced()
    {
        system.RequireReady();
        var tom = DemoUsers.Tom;
        var orders = await GetAsync<Paged<OrderSummary>>("acme", tom, "/api/v1/purchasing/orders?pageSize=100");
        var po = orders.Items.Single(o => o.Number == "PO-1001");
        po.SupplierId.ShouldBe("fabrikam-office");
        po.Status.ShouldBe("Received");

        var order = await GetAsync<OrderView>("acme", tom, $"/api/v1/purchasing/orders/{po.Id}");
        order.Lines.Select(l => (l.Sku, l.Ordered, l.Received)).ShouldBe([("DOCK-USB", 2, 2), ("STAND-LP", 2, 2)]);
        var request = await GetAsync<SeededRequest>("acme", tom, $"/api/v1/requests/{order.RequestId}");
        request.Title.ShouldBe(AllScenarios.Single(s => s.Tenant == "acme").Requests.First(r => r.IsOrdered).Title);

        var invoices = await GetAsync<Paged<InvoiceView>>("acme", DemoUsers.Thandi, $"/api/v1/invoicing/invoices?orderId={po.Id}");
        invoices.Items.ShouldBeEmpty("Fabrikam posts PDF invoices: PO-1001 waits for the reviewer's scan");
    }

    /// <summary>
    /// The nightly reset (plan 06 ruling 4, Review Focus 2): a reviewer's extra request, then reset and seed twice. Both resets end in the same
    /// data (same requests with the same numbers and states, PO-1001 again the Fabrikam order, same budgets and invoices), every seeded request
    /// has its approval chain and reservation again, and nothing the first reset deleted is flagged as changed outside the app (ruling 5,
    /// checked before Audit itself is cleared).
    /// </summary>
    [Fact]
    public async Task Reset_twice_gives_identical_counts()
    {
        system.RequireReady();
        var extra = await CreateExtraRequestAsync("Extra chairs a reviewer added");

        // The first reset checks the deletes themselves (before Audit is cleared); the second runs exactly like the tool (Audit right after
        // the grace period) and checks that nothing the services' resets caused is left half-matched after Audit's reset.
        await ResetAndSeedAsync(checkDeletes: true);
        var first = await StableSnapshotAsync();
        await ResetAndSeedAsync(checkAfterSeed: true);
        var second = await StableSnapshotAsync();

        second.ShouldBe(first);
        first.ShouldNotContain(line => line.Contains(extra.Title, StringComparison.Ordinal));
        first.ShouldContain(line => line.StartsWith("acme request PR-0001 ", StringComparison.Ordinal));
        await Seed_produces_every_state_in_both_tenants();
        await Po_1001_is_received_and_not_invoiced();
    }

    /// <summary>
    /// After a reset the lists show the new seed at once: no cached list (the 1 s micro-cache, the stored Idempotency-Key answers, the Access
    /// answers) still shows the request a reviewer added before (Review Focus 2).
    /// </summary>
    [Fact]
    public async Task Lists_show_new_seed_after_reset()
    {
        system.RequireReady();
        var tom = DemoUsers.Tom;
        var extra = await CreateExtraRequestAsync("Extra monitor a reviewer added");
        await SystemFlow.EventuallyAsync(
            () => RequestsOfAsync("acme", tom), list => list.Any(r => r.Id == extra.Id), "the extra request listed", TimeSpan.FromSeconds(10));

        await ResetAndSeedAsync();

        var listed = await RequestsOfAsync("acme", tom);
        listed.ShouldNotContain(r => r.Id == extra.Id || r.Title == extra.Title);
        var expected = AllScenarios.Single(s => s.Tenant == "acme").Requests.Where(r => DemoUsers.All[r.Requester] == tom).Select(r => r.Title);
        listed.Select(r => r.Title).ShouldBe(expected, ignoreOrder: true);
        (await GetAsync<Paged<SeededRequest>>("acme", null, "/api/v1/requests?pageSize=100")).Items.Min(r => r.Number).ShouldBe("PR-0001");
    }

    private async Task<SeededRequest> CreateExtraRequestAsync(string title)
    {
        using var http = system.Gateway();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/requests")
        {
            Content = JsonContent.Create(new
            {
                title,
                costCenterCode = "IT-200",
                supplierId = "contoso-hardware",
                lines = new[] { new { sku = "MON-27", quantity = 1 } },
            }),
        };
        message.Headers.Add("X-Api-Key", system.AdminApiKey);
        message.Headers.Add("X-Tenant-Id", "acme");
        message.Headers.Add("X-On-Behalf-Of-User", DemoUsers.Tom);
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await http.SendAsync(message, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        return System.Text.Json.JsonSerializer.Deserialize<SeededRequest>(body, GatewayClient.Json)!;
    }

    /// <summary>
    /// The reset as <c>ProcureAi.DemoData reset</c> runs it (simulators, the services, Audit after the grace period), then the seed.
    /// <paramref name="checkDeletes"/>: between the services and Audit, every change Audit got (the deletes too) is attributed to the app.
    /// <paramref name="checkAfterSeed"/>: after the seed, every change Audit has is attributed to the app (none left half-matched by Audit's
    /// own reset).
    /// </summary>
    private async Task ResetAndSeedAsync(bool checkDeletes = false, bool checkAfterSeed = false)
    {
        var clock = Stopwatch.StartNew();
        var resetter = Resetter();
        (await resetter.ResetServicesAsync(TestContext.Current.CancellationToken)).ShouldBeTrue("a service's reset failed");
        if (checkDeletes)
        {
            await new SystemFlow(system).NoOutsideAppChangesAsync();
        }

        (await resetter.ResetAuditAsync(TestContext.Current.CancellationToken)).ShouldBeTrue("the reset of Audit failed");
        var reset = clock.Elapsed;

        using var watching = new CancellationTokenSource();
        var invoicingLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var watch = WatchAsync("invoicing-api", invoicingLog, watching.Token);
        var results = await Task.WhenAll(AllScenarios.Select(s => Runner().RunAsync(s, TestContext.Current.CancellationToken)));
        await watching.CancelAsync();
        await watch;
        var failures = results.SelectMany(r => r.Failures).Select(f => f.Message).ToList();
        failures.ShouldBeEmpty(
            "invoicing-api errors during the seed:" + Environment.NewLine
            + string.Join(Environment.NewLine, invoicingLog.Where(l => l.Contains("fail", StringComparison.OrdinalIgnoreCase)
                || l.Contains("Exception", StringComparison.Ordinal) || l.StartsWith("   at ", StringComparison.Ordinal)).Take(80)));
        foreach (var result in results)
        {
            result.Created.ShouldBe(AllScenarios.Single(s => s.Tenant == result.Tenant).Requests.Count, result.Tenant);
        }

        TestContext.Current.SendDiagnosticMessage(
            $"Demo reset {reset.TotalSeconds:0} s, seed {(clock.Elapsed - reset).TotalSeconds:0} s, total {clock.Elapsed.TotalSeconds:0} s");
        if (checkAfterSeed)
        {
            await new SystemFlow(system).NoOutsideAppChangesAsync();
        }
    }

    /// <summary>Collects a resource's console log lines until cancelled.</summary>
    private async Task WatchAsync(string resource, System.Collections.Concurrent.ConcurrentQueue<string> lines, CancellationToken cancellationToken)
    {
        try
        {
            var logs = system.App.Services.GetRequiredService<Aspire.Hosting.ApplicationModel.ResourceLoggerService>();
            await foreach (var batch in logs.WatchAsync(resource).WithCancellation(cancellationToken))
            {
                foreach (var line in batch)
                {
                    lines.Enqueue(line.Content);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A snapshot once two reads 3 s apart agree (the last payments' budget spend lands shortly after the seed ends).</summary>
    private async Task<IReadOnlyList<string>> StableSnapshotAsync()
    {
        var previous = await SnapshotAsync();
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            var current = await SnapshotAsync();
            if (current.SequenceEqual(previous))
            {
                return current;
            }

            previous = current;
        }

        return previous;
    }

    /// <summary>Every request of the scenario tenants (number, title, status, order), every budget's reservations and spend, every invoice.</summary>
    private async Task<IReadOnlyList<string>> SnapshotAsync()
    {
        var lines = new List<string>();
        foreach (var tenant in AllScenarios.Select(s => s.Tenant))
        {
            // Requests are created one by one (their numbers are fixed) and PO-1001 is driven alone; the other orders and the supplier's
            // invoice numbers follow the order the parallel seed reaches the supplier, so they are keyed by their request's title here.
            var requests = await GetAsync<Paged<SeededRequest>>(tenant, null, "/api/v1/requests?pageSize=100");
            lines.AddRange(requests.Items.OrderBy(r => r.Number, StringComparer.Ordinal)
                .Select(r => $"{tenant} request {r.Number} {r.Title} {r.Status} {(r.OrderNumber is "PO-1001" or null ? r.OrderNumber : "ordered")}"));
            var titles = requests.Items.Where(r => r.OrderId is not null).ToDictionary(r => r.OrderId!.Value, r => r.Title);
            var budgets = await GetAsync<List<BudgetView>>(tenant, null, "/api/v1/budgets");
            lines.AddRange(budgets.OrderBy(b => b.CostCenterCode + b.FiscalYear, StringComparer.Ordinal)
                .Select(b => $"{tenant} budget {b.CostCenterCode} {b.FiscalYear} reserved {b.Reserved} spent {b.Spent}"));
            var invoices = await GetAsync<Paged<InvoiceView>>(tenant, null, "/api/v1/invoicing/invoices?pageSize=100");
            lines.AddRange(invoices.Items
                .Select(i => $"{tenant} invoice of {titles.GetValueOrDefault(i.OrderId, "another order")} {i.Source} {i.Status}")
                .OrderBy(l => l, StringComparer.Ordinal));
        }

        return lines;
    }

    private ResetRunner Resetter()
    {
        var simulatorUrl = system.App.GetEndpoint("simulators", "http");
        var options = Options.Create(new DemoDataOptions
        {
            GatewayUrl = system.Gateway().BaseAddress!.ToString(),
            SimulatorUrl = simulatorUrl.ToString(),
            AdminApiKey = system.AdminApiKey,
            ResetCallTimeout = TimeSpan.FromMinutes(2),
            ResetRetries = 3,
            ResetRetryDelay = TimeSpan.FromSeconds(2),
            AuditGrace = TimeSpan.FromSeconds(10),
        });
        var simulators = new HttpClient { BaseAddress = simulatorUrl };
        return new ResetRunner(new ResetClients(system.Gateway(), simulators), options, TimeProvider.System, NullLogger<ResetRunner>.Instance);
    }

    private sealed record BudgetView(string CostCenterCode, string FiscalYear, decimal Reserved, decimal Spent);

    private static string RequestStatus(StopAt stop) => stop switch
    {
        StopAt.Draft => "Draft",
        StopAt.Submitted or StopAt.AwaitingDepartmentHead or StopAt.AwaitingFinance => "Submitted",
        StopAt.Rejected => "Rejected",
        StopAt.Cancelled => "Cancelled",
        StopAt.Completed or StopAt.ClosedShort => "Completed",
        _ => "Ordered",
    };

    private async Task StateMatchesAsync(Scenario scenario, ScenarioRequest expected, SeededRequest request, string what)
    {
        var tenant = scenario.Tenant;
        var requester = request.RequesterId;
        switch (expected.StopAt)
        {
            case StopAt.Submitted or StopAt.AwaitingDepartmentHead or StopAt.AwaitingFinance:
                var chain = await GetAsync<ChainView>(tenant, requester, $"/api/v1/approvals/requests/{request.Id}");
                var open = chain.Steps.Single(s => s.Status == "Pending");
                open.Level.ShouldBe(expected.StopAt switch
                {
                    StopAt.AwaitingDepartmentHead => "DepartmentHead",
                    StopAt.AwaitingFinance => "Finance",
                    _ => open.Level,
                }, what);
                // Submitted waits at its first step; the others after the steps their approvers approved.
                open.StepNo.ShouldBe(expected.StopAt == StopAt.Submitted ? 1 : expected.Approvers!.Count + 1, what);
                return;
            case StopAt.Rejected:
                (await RejectionAsync(tenant, requester, request.Id)).ShouldNotBeNullOrWhiteSpace(what);
                return;
            case < StopAt.Ordered:
                return;
        }

        var order = await GetAsync<OrderView>(tenant, requester, $"/api/v1/purchasing/requests/{request.Id}/order");
        order.Status.ShouldBe(expected.StopAt switch
        {
            StopAt.Ordered => "Confirmed",
            StopAt.PartiallyReceived => "PartiallyReceived",
            StopAt.ClosedShort => "ClosedShort",
            _ => "Received",
        }, what);
        var invoices = (await GetAsync<Paged<InvoiceView>>(tenant, DemoUsers.All[scenario.Finance], $"/api/v1/invoicing/invoices?orderId={order.Id}")).Items;
        string? invoiceStatus = expected.StopAt switch
        {
            StopAt.Invoiced => "Matched",
            StopAt.Mismatched => "Mismatched",
            StopAt.PartiallyPaid => "PartiallyPaid",
            StopAt.Completed => "Paid",
            StopAt.ClosedShort => "PartiallyPaid", // 4 invoiced, the 3 received paid, the 4th closed short
            _ => null,
        };
        if (invoiceStatus is not null)
        {
            invoices.ShouldHaveSingleItem(what).Status.ShouldBe(invoiceStatus, what);
        }
        else if (Scenarios.ManualInvoiceSuppliers.Contains(expected.Supplier))
        {
            invoices.ShouldBeEmpty(what);
        }
    }

    private async Task<string?> RejectionAsync(string tenant, string requester, Guid id) =>
        (await GetAsync<RequestDetail>(tenant, requester, $"/api/v1/requests/{id}")).RejectionReason;

    /// <summary>
    /// For every scenario request: how many requests its requester has with its title, how many orders and invoices it has. Only the seed's
    /// own records: the other system tests' orders may still get their supplier's invoice while this runs.
    /// </summary>
    private async Task<IReadOnlyList<string>> CountsAsync()
    {
        var counts = new List<string>();
        foreach (var scenario in AllScenarios)
        {
            var finance = DemoUsers.All[scenario.Finance];
            foreach (var expected in scenario.Requests)
            {
                var requester = DemoUsers.All[expected.Requester];
                var titled = (await RequestsOfAsync(scenario.Tenant, requester)).Where(r => r.Title == expected.Title).ToList();
                var orders = 0;
                var invoices = 0;
                foreach (var order in titled.Where(r => r.OrderId is not null))
                {
                    orders++;
                    invoices += (await GetAsync<Paged<InvoiceView>>(scenario.Tenant, finance, $"/api/v1/invoicing/invoices?orderId={order.OrderId}")).Total;
                }

                counts.Add($"{scenario.Tenant}/{expected.Key}: {titled.Count} requests, {orders} orders, {invoices} invoices");
            }
        }

        return counts;
    }

    private async Task<IReadOnlyList<SeededRequest>> RequestsOfAsync(string tenant, string requester) =>
        (await GetAsync<Paged<SeededRequest>>(tenant, requester, "/api/v1/requests?mine=true&pageSize=100")).Items;

    /// <summary>A read through the gateway with the admin key, on behalf of <paramref name="actAs"/> (null: the key itself) in a tenant.</summary>
    private async Task<T> GetAsync<T>(string tenant, string? actAs, string path)
    {
        using var http = system.Gateway();
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.Add("X-Api-Key", system.AdminApiKey);
        message.Headers.Add("X-Tenant-Id", tenant);
        if (actAs is not null)
        {
            message.Headers.Add("X-On-Behalf-Of-User", actAs);
        }

        using var response = await http.SendAsync(message, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET {path}: {body}");
        return System.Text.Json.JsonSerializer.Deserialize<T>(body, GatewayClient.Json)!;
    }

    /// <summary>The seeder as the AppHost runs it, in-process, against the running gateway.</summary>
    private ScenarioRunner Runner()
    {
        var options = Options.Create(new DemoDataOptions
        {
            GatewayUrl = system.Gateway().BaseAddress!.ToString(),
            AdminApiKey = system.AdminApiKey,
            StepTimeout = TimeSpan.FromSeconds(90),
            PollInterval = TimeSpan.FromMilliseconds(1100),
            MaxParallel = 4,
            ScenarioDirectory = "Scenarios",
            AllowUntrustedServerCertificate = true,
        });
        var gateway = new GatewayClient(ResilientGateway(), options);
        return new ScenarioRunner(gateway, options, TimeProvider.System, NullLogger<ScenarioRunner>.Instance);
    }

    /// <summary>
    /// A gateway client with the standard resilience handler, as ProcureAi.DemoData registers it: a 5xx while the whole stack is busy (the
    /// shared Postgres has been seen at its connection limit, 53300, during a seed) is retried like the tool retries it.
    /// </summary>
    private HttpClient ResilientGateway()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("gateway")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            })
            .AddStandardResilienceHandler();
        var http = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("gateway");
        http.BaseAddress = system.Gateway().BaseAddress;
        http.Timeout = Timeout.InfiniteTimeSpan;
        return http;
    }

    private sealed record OrderSummary(Guid Id, string Number, string SupplierId, string Status);

    private sealed record RequestDetail(string? RejectionReason);
}
