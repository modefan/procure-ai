using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shouldly;

namespace ProcureAi.SystemTests;

/// <summary>
/// The supplier goes down before a request is approved (the simulator's chaos switch): the order is created, sending it fails and is
/// retried with backoff, the supplier circuit opens (Polly's metric reaches the OTLP endpoint), and nothing is lost. When the supplier
/// is back the order is sent once and confirmed once.
/// </summary>
[Collection(SystemCollection.Name)]
public sealed partial class SupplierOutageTests(SystemFixture system)
{
    // 1 × KB-WL at R 650,00 = R 747,50 incl. VAT: Maria alone approves.
    private const decimal KeyboardInclVat = 747.50m;

    // Backoff 5 s doubling (jitter 0.5) and a 20 s circuit break: two failed attempts within about 25 s, the recovery within a minute or two.
    private static readonly TimeSpan OutageTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan MetricTimeout = TimeSpan.FromSeconds(90);

    private readonly SystemFlow _flow = new(system);

    [Fact]
    public async Task Supplier_outage_is_survived()
    {
        system.RequireReady();
        using var simulators = system.Simulators();
        var (normal, _) = await simulators.GetAsync<JsonObject>("/api/v1/simulators/chaos");
        var down = normal.DeepClone().AsObject();
        down["supplier"]!["mode"] = "Down";
        var downSince = DateTimeOffset.UtcNow;
        await simulators.SendAsync<JsonObject>(HttpMethod.Put, "/api/v1/simulators/chaos", HttpStatusCode.OK, down);
        try
        {
            var request = await _flow.SubmitAsync("Keyboard for the reception", "KB-WL", 1, KeyboardInclVat);
            await _flow.ApproveAsync(SystemFixture.Maria, request.Id, 1, "Manager", "R 747,50");

            // The order exists (the request is Ordered) but the supplier cannot be reached: attempts and the last error show on it.
            await _flow.RequestWhenAsync(request.Id, r => r.Status == "Ordered", "request ordered while the supplier is down");
            var (pending, _) = await _flow.OrderWhenAsync(
                request.Id, o => o.Status == "PendingSupplier" && FailedAttempts(o) >= 2, "two failed attempts to reach the supplier", OutageTimeout);
            pending.SupplierStatus.ShouldStartWith("Supplier unavailable · retrying");
            pending.SupplierReference.ShouldBeNull();

            // The supplier circuit opened: Polly's resilience.polly.strategy.events (event.name=OnCircuitOpened) from purchasing-api.
            var opened = await SystemFlow.EventuallyAsync(
                () => Task.FromResult(system.Telemetry.FirstSeen(SystemFixture.SupplierCircuitOpened)),
                at => at is not null,
                $"the circuit-opened metric exported over OTLP ({system.Telemetry.MetricExports} metric exports so far)",
                MetricTimeout);
            opened!.Value.ShouldBeGreaterThan(downSince);

            // The supplier is back: the next attempt (after the backoff and the circuit's break) gets through and it confirms.
            await simulators.SendAsync<JsonObject>(HttpMethod.Put, "/api/v1/simulators/chaos", HttpStatusCode.OK, normal);
            var (confirmed, _) = await _flow.OrderWhenAsync(request.Id, o => o.Status == "Confirmed", "order confirmed after the outage", RecoveryTimeout);
            confirmed.SupplierReference.ShouldNotBeNullOrWhiteSpace();

            // Sent once, confirmed once: one supplier order (the simulator deduplicates by our order ID) and one confirmation.
            var history = await _flow.HistoryWhenAsync(request.Id, h => h.Any(e => e.Type == "PurchaseOrderConfirmed"), "confirmation in the audit");
            history.Count(e => e.Type == "PurchaseOrderCreated").ShouldBe(1);
            history.Count(e => e.Type == "PurchaseOrderConfirmed").ShouldBe(1);
            history.Single(e => e.Type == "PurchaseOrderConfirmed").Summary.ShouldContain(confirmed.SupplierReference!);
            (await _flow.RequestWhenAsync(request.Id, _ => true, "request")).Body.Status.ShouldBe("Ordered");
        }
        finally
        {
            // Never leave the supplier down for the tests that follow.
            await simulators.SendAsync<JsonObject>(HttpMethod.Put, "/api/v1/simulators/chaos", HttpStatusCode.OK, normal);
        }
    }

    /// <summary>The failed attempts so far, from "Supplier unavailable · retrying (attempt N)" (N is the next attempt).</summary>
    private static int FailedAttempts(OrderView order) =>
        AttemptPattern().Match(order.SupplierStatus) is { Success: true } match ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1 : 0;

    [GeneratedRegex(@"\(attempt (\d+)\)")]
    private static partial Regex AttemptPattern();
}
