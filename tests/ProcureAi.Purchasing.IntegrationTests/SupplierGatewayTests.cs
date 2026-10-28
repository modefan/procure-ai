using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Purchasing.Application.Abstractions;
using ProcureAi.Purchasing.Domain;
using ProcureAi.Purchasing.Infrastructure;
using ProcureAi.Purchasing.Infrastructure.Supplier;
using Shouldly;

namespace ProcureAi.Purchasing.IntegrationTests;

/// <summary>
/// The supplier anti-corruption layer on its own (no containers): the wire format it sends, how it reads the supplier's answers,
/// and its resilience pipeline as registered by <c>AddSupplierGateway</c> (retries, circuit breaker, timeouts), against a fake
/// supplier.
/// </summary>
public sealed class SupplierGatewayTests
{
    private static readonly SupplierOrder Order = new(
        Guid.Parse("0199a8f0-0000-7000-8000-000000000001"), "acme", "PO-1001", "contoso-hardware",
        [new OrderLine(1, "LAP-14", "Laptop", 3, Money.Zar(25_200m)), new OrderLine(2, "PEN-01", "Pen", 10, Money.Zar(12.345m))]);

    private sealed class FakeSupplier(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private int _calls;

        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add((request, body));
            }

            return await answer(request, Interlocked.Increment(ref _calls), cancellationToken);
        }
    }

    private static ServiceProvider Build(FakeSupplier supplier, Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["SupplierClient:BaseUrl"] = "http://supplier.test/",
            ["SupplierClient:ApiKey"] = "the-key",
            ["SupplierClient:AttemptTimeout"] = "00:00:00.300",
            ["SupplierClient:RetryCount"] = "2",
            ["SupplierClient:RetryDelay"] = "00:00:00.010",
            ["SupplierClient:TotalTimeout"] = "00:00:05",
            ["SupplierClient:CircuitBreaker:FailureRatio"] = "0.5",
            ["SupplierClient:CircuitBreaker:MinimumThroughput"] = "4",
            ["SupplierClient:CircuitBreaker:SamplingDuration"] = "00:00:30",
            ["SupplierClient:CircuitBreaker:BreakDuration"] = "00:00:20",
        };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSupplierGateway(configuration);
        services.AddHttpClient(SupplierSimulatorGateway.ClientName).ConfigurePrimaryHttpMessageHandler(() => supplier);
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Order_goes_out_in_the_suppliers_vocabulary_with_the_key_and_the_acceptance_comes_back()
    {
        var supplier = new FakeSupplier((_, _, _) => Task.FromResult(Json(HttpStatusCode.Accepted, """{"supplierOrderId":"SO-1001","status":"RECEIVED"}""")));
        await using var services = Build(supplier);

        var acceptance = await services.GetRequiredService<ISupplierGateway>().SubmitOrderAsync(Order, CancellationToken.None);

        acceptance.SupplierReference.ShouldBe("SO-1001");
        var (request, body) = supplier.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe("http://supplier.test/supplier/v1/orders");
        request.Headers.GetValues("X-Api-Key").ShouldBe(["the-key"]);
        var json = JsonDocument.Parse(body!).RootElement;
        json.GetProperty("externalReference").GetString().ShouldBe(Order.OrderId.ToString(), "the supplier deduplicates by our order ID");
        json.GetProperty("buyerReference").GetString().ShouldBe("acme/PO-1001");
        json.GetProperty("supplierCode").GetString().ShouldBe("contoso-hardware", "the supplier quotes it on its invoices");
        json.GetProperty("currency").GetString().ShouldBe("ZAR");
        var lines = json.GetProperty("lines").EnumerateArray().ToList();
        lines[0].GetProperty("articleNumber").GetString().ShouldBe("LAP-14");
        lines[0].GetProperty("lineNumber").GetInt32().ShouldBe(1);
        lines[0].GetProperty("quantity").GetInt32().ShouldBe(3);
        lines[0].GetProperty("netPriceCents").GetInt64().ShouldBe(2_520_000);
        lines[1].GetProperty("netPriceCents").GetInt64().ShouldBe(1235, "rounded to cents, half away from zero");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_refusal_is_rejected_for_good_with_the_suppliers_message_and_not_retried(HttpStatusCode status)
    {
        var supplier = new FakeSupplier((_, _, _) => Task.FromResult(Json(
            status, """{"title":"One or more validation errors occurred.","status":400,"errors":{"Currency":["Only ZAR is supported."]}}""")));
        await using var services = Build(supplier);

        var refused = await Should.ThrowAsync<SupplierRejectedException>(
            () => services.GetRequiredService<ISupplierGateway>().SubmitOrderAsync(Order, CancellationToken.None));

        refused.Message.ShouldBe("The supplier refused it: Only ZAR is supported.");
        supplier.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "status_503")]
    [InlineData(HttpStatusCode.InternalServerError, "status_500")]
    [InlineData(HttpStatusCode.TooManyRequests, "status_429")]
    [InlineData(HttpStatusCode.RequestTimeout, "status_408")]
    [InlineData(HttpStatusCode.Unauthorized, "status_401")]
    public async Task A_failing_supplier_is_unavailable_after_the_retries(HttpStatusCode status, string reason)
    {
        var supplier = new FakeSupplier((_, _, _) => Task.FromResult(new HttpResponseMessage(status)));
        await using var services = Build(supplier);

        var unavailable = await Should.ThrowAsync<SupplierUnavailableException>(
            () => services.GetRequiredService<ISupplierGateway>().SubmitOrderAsync(Order, CancellationToken.None));

        unavailable.Reason.ShouldBe(reason);
        supplier.Calls.ShouldBe(status == HttpStatusCode.Unauthorized ? 1 : 3, "transient answers are retried twice");
    }

    [Fact]
    public async Task No_connection_is_unreachable_a_slow_supplier_a_timeout_and_a_garbled_acceptance_a_retry()
    {
        var refused = new FakeSupplier((_, _, _) => throw new HttpRequestException("Connection refused"));
        var slow = new FakeSupplier(async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var garbled = new FakeSupplier((_, _, _) => Task.FromResult(Json(HttpStatusCode.Accepted, "<html>accepted</html>")));
        await using var refusing = Build(refused);
        await using var slowServices = Build(slow, new() { ["SupplierClient:TotalTimeout"] = "00:00:01" });
        await using var garbledServices = Build(garbled);

        var unreachable = await Should.ThrowAsync<SupplierUnavailableException>(
            () => refusing.GetRequiredService<ISupplierGateway>().SubmitOrderAsync(Order, CancellationToken.None));
        var timeout = await Should.ThrowAsync<SupplierUnavailableException>(
            () => slowServices.GetRequiredService<ISupplierGateway>().SubmitOrderAsync(Order, CancellationToken.None));

        var unreadable = await Should.ThrowAsync<SupplierUnavailableException>(
            () => garbledServices.GetRequiredService<ISupplierGateway>().SubmitOrderAsync(Order, CancellationToken.None));

        unreachable.Reason.ShouldBe("unreachable");
        timeout.Reason.ShouldBe("timeout");
        unreadable.Reason.ShouldBe("bad_answer", "accepted, but under an unknown reference: ask again");
    }

    [Fact]
    public async Task Repeated_failures_open_the_circuit_and_calls_stop_reaching_the_supplier()
    {
        var supplier = new FakeSupplier((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await using var services = Build(supplier);
        var gateway = services.GetRequiredService<ISupplierGateway>();

        var reasons = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            reasons.Add((await Should.ThrowAsync<SupplierUnavailableException>(() => gateway.SubmitOrderAsync(Order, CancellationToken.None))).Reason);
        }

        var callsWhenOpen = supplier.Calls;
        reasons.Last().ShouldBe("circuit_open");
        (await Should.ThrowAsync<SupplierUnavailableException>(() => gateway.SubmitOrderAsync(Order, CancellationToken.None))).Reason.ShouldBe("circuit_open");
        supplier.Calls.ShouldBe(callsWhenOpen, "an open circuit fails fast without calling the supplier");
    }

    [Fact]
    public async Task Cancel_of_an_order_the_supplier_does_not_know_is_rejected_and_a_known_one_is_posted()
    {
        var supplier = new FakeSupplier((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("SO-404") ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(HttpStatusCode.OK, """{"status":"CANCELLED"}""")));
        await using var services = Build(supplier);
        var gateway = services.GetRequiredService<ISupplierGateway>();

        await gateway.CancelOrderAsync("SO 1/2", CancellationToken.None);
        await Should.ThrowAsync<SupplierRejectedException>(() => gateway.CancelOrderAsync("SO-404", CancellationToken.None));

        supplier.Requests[0].Request.RequestUri!.AbsoluteUri.ShouldBe("http://supplier.test/supplier/v1/orders/SO%201%2F2/cancel");
        supplier.Requests[0].Request.Method.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public void Invalid_supplier_configuration_fails_when_the_options_are_read()
    {
        var supplier = new FakeSupplier((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var services = Build(supplier, new() { ["SupplierClient:ApiKey"] = "", ["SupplierClient:CircuitBreaker:FailureRatio"] = "2" });

        var failure = Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(
            () => services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SupplierClientOptions>>().Value);

        failure.Failures.ShouldContain(f => f.Contains("ApiKey"));
        failure.Failures.ShouldContain(f => f.Contains("FailureRatio"));
    }
}
