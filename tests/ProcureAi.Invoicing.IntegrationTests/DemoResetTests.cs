using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Messaging;
using Shouldly;
using Wolverine;

namespace ProcureAi.Invoicing.IntegrationTests;

/// <summary>
/// The nightly demo reset of Invoicing (plan 06 rulings 4 and 5) on an instance with a database of its own: the order copies, invoices with
/// their lines, payments and the processed-webhook records of every tenant are deleted with ordinary deletes the interceptor records.
/// </summary>
[Collection(InvoicingCollection.Name)]
public sealed class DemoResetTests(InvoicingApp app)
{
    [Fact]
    public async Task Demo_reset_clears_business_rows_and_reseeds()
    {
        var database = await app.Infra.CreateMigratedDatabaseAsync($"reset_{Guid.NewGuid():N}");
        await using var factory = new InvoicingApiFactory(
            app.Infra, app.Access, app.Assistant, app.Erp, [], app.ReadPause, database,
            new Dictionary<string, string?> { ["DemoReset:Enabled"] = "true" });
        using var client = factory.CreateClient();
        var bus = factory.Services.GetRequiredService<IMessageBus>();
        var invoices = 0;
        foreach (var tenant in new[] { InvoicingApp.NewTenant(), InvoicingApp.NewTenant() })
        {
            var order = InvoicingApp.NewOrder(tenant);
            await bus.InvokeAsync(order, new DeliveryOptions().WithHeader(CallerHeaders.TenantId, tenant).WithHeader(CallerHeaders.UserId, "service-purchasing"));
            using var webhook = new HttpRequestMessage(HttpMethod.Post, InvoicingApp.WebhookPath)
            {
                Content = new ByteArrayContent(InvoicingApp.InvoiceEvent(order.OrderId, $"INV-{tenant}", 3)),
            };
            webhook.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            WebhookTestSigner.Sign(webhook, SimulatorHost.Secret, DateTimeOffset.UtcNow);
            using var response = await client.SendAsync(webhook, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            invoices++;
        }

        (await DemoResetProbe.CountAsync(database, "select count(*) from invoices")).ShouldBe(invoices);

        (await DemoResetProbe.ResetAsync(client, "invoicing")).ShouldBe(HttpStatusCode.NoContent);

        foreach (var table in new[] { "order_views", "order_view_lines", "invoices", "invoice_lines", "payments", "processed_webhooks", "tenant_sequences" })
        {
            (await DemoResetProbe.CountAsync(database, $"select count(*) from {table}")).ShouldBe(0, table);
        }

        // One interceptor row per deleted invoice and invoice line, in one transaction (ruling 5).
        var deletes = await DemoResetProbe.DeleteEntriesAsync(database, "invoices");
        deletes.Count.ShouldBe(invoices);
        (await DemoResetProbe.DeleteEntriesAsync(database, "invoice_lines")).Count.ShouldBe(invoices);
        (await DemoResetProbe.ResetAsync(client, "invoicing")).ShouldBe(HttpStatusCode.NoContent, "twice is safe");
    }
}
