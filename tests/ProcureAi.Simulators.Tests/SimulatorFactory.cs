using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ProcureAi.BuildingBlocks.Web.Webhooks;
using ProcureAi.Simulators.Webhooks;

namespace ProcureAi.Simulators.Tests;

/// <summary>A webhook the fake receiver got.</summary>
public sealed record ReceivedWebhook(Uri Url, string? Signature, byte[] Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    public string Type => Json.GetProperty("type").GetString()!;
}

/// <summary>Stands in for Purchasing and Invoicing: records every webhook and answers with a status the test chooses.</summary>
public sealed class FakeWebhookReceiver : HttpMessageHandler
{
    private readonly ConcurrentQueue<ReceivedWebhook> _received = new();

    /// <summary>The status to answer with, given the webhook and how many requests the receiver has now seen (this one included).</summary>
    public Func<ReceivedWebhook, int, HttpStatusCode> Respond { get; set; } = (_, _) => HttpStatusCode.OK;

    public IReadOnlyList<ReceivedWebhook> Received => [.. _received];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        request.Headers.TryGetValues(WebhookSignature.HeaderName, out var values);
        var webhook = new ReceivedWebhook(request.RequestUri!, values?.SingleOrDefault(), body);
        _received.Enqueue(webhook);
        return new HttpResponseMessage(Respond(webhook, _received.Count));
    }
}

/// <summary>The real simulator application with a fake clock and a fake receiver in place of the network.</summary>
public sealed class SimulatorFactory : WebApplicationFactory<Program>
{
    public const string SimulatorKey = "test-simulator-key";
    public const string AdminKey = "test-admin-key";
    public const string Secret = "test-webhook-secret-0123456789abcdef";
    public const string PurchasingUrl = "http://purchasing.test/webhooks/supplier";
    public const string InvoicingUrl = "http://invoicing.test/webhooks/supplier";

    private readonly Dictionary<string, string?> _settings;

    /// <param name="start">The fake clock's start (a later one stands for a simulator started again later).</param>
    public SimulatorFactory(Dictionary<string, string?>? overrides = null, DateTimeOffset? start = null)
    {
        Time = new FakeTimeProvider(start ?? DefaultStart);
        _settings = new Dictionary<string, string?>
        {
            ["Simulators:ApiKey"] = SimulatorKey,
            ["Auth:AdminApiKeySha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AdminKey))),
            ["Simulators:Webhooks:PurchasingUrl"] = PurchasingUrl,
            ["Simulators:Webhooks:InvoicingUrl"] = InvoicingUrl,
            ["Simulators:Webhooks:Secret"] = Secret,
            ["Simulators:Random:Seed"] = "42",
            // No latency unless a test asks for it: the fake clock would otherwise have to be advanced for every call.
            ["Simulators:Chaos:Supplier:MinLatency"] = "00:00:00",
            ["Simulators:Chaos:Supplier:MaxLatency"] = "00:00:00",
            ["Simulators:Chaos:Erp:MinLatency"] = "00:00:00",
            ["Simulators:Chaos:Erp:MaxLatency"] = "00:00:00",
        };
        foreach (var pair in overrides ?? [])
        {
            _settings[pair.Key] = pair.Value;
        }
    }

    public static readonly DateTimeOffset DefaultStart = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Time { get; }

    public FakeWebhookReceiver Receiver { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.AddHttpClient(WebhookSender.ClientName).ConfigurePrimaryHttpMessageHandler(() => Receiver);
        });
    }

    public HttpClient SupplierClient() => ClientWithKey(SimulatorKey);

    public HttpClient AdminClient() => ClientWithKey(AdminKey);

    public HttpClient ClientWithKey(string? key)
    {
        var client = CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        return client;
    }

    /// <summary>Advances the fake clock step by step (a timer may be registered a moment after the call) until the condition holds.</summary>
    public async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan step)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
            {
                return;
            }

            Time.Advance(step);
            await Task.Delay(5);
        }

        throw new TimeoutException("The condition never became true while the clock advanced.");
    }

    /// <summary>Lets pending work run for a moment of real time, for asserting that something does NOT happen.</summary>
    public static Task SettleAsync() => Task.Delay(100);

    /// <summary>Completes a request that waits on the fake clock (a latency) by advancing the clock until it is done.</summary>
    public async Task<T> WithClockAsync<T>(Task<T> request)
    {
        for (var i = 0; i < 400 && !request.IsCompleted; i++)
        {
            Time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(2);
        }

        return await request;
    }

    public static object OrderBody(string externalReference, params (int Line, string Article, int Quantity, long PriceCents)[] lines) => new
    {
        externalReference,
        buyerReference = "demo/PO-0001",
        currency = "ZAR",
        lines = lines.Select(l => new { lineNumber = l.Line, articleNumber = l.Article, quantity = l.Quantity, netPriceCents = l.PriceCents }),
    };

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
}
