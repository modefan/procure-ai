using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ProcureAi.Invoicing.Application.Dtos;
using ProcureAi.Invoicing.Domain;
using Shouldly;

namespace ProcureAi.Invoicing.IntegrationTests;

/// <summary>
/// An invoice recorded from a scan (plan 05 Task 6): with <c>scanId</c> finance's reviewed lines are saved as source <c>Scan</c>, with the
/// scan the Assistant stored (read through its internal endpoint, for the caller's tenant) as the invoice's document. A scan that is not
/// the caller's tenant's is a 422 to the caller, the same as one that does not exist.
/// </summary>
[Collection(InvoicingCollection.Name)]
public sealed class ScannedInvoiceTests(InvoicingApp app)
{
    private const string Url = "/api/v1/invoicing/invoices";

    /// <summary>A scan as the Assistant returns it: what the model read, here deliberately not what finance enters.</summary>
    private static object Scan(Guid id, string number) => new
    {
        id,
        supplierName = "Contoso Hardware (Pty) Ltd",
        supplierVatNumber = "4123456789",
        invoiceNumber = number,
        invoiceDate = "2026-10-20",
        orderNumber = "PO-1001",
        lines = new[] { new { description = "Laptop 14 inch", quantity = 9m, unitPriceExclVat = 1m, lineTotalExclVat = 9m } },
        vat = 1.35m,
        totalExclVat = 9m,
        totalInclVat = 10.35m,
        confidence = "high",
        warnings = Array.Empty<object>(),
        model = "fake-chat",
    };

    private static object Body(Guid orderId, string number, Guid? scanId, int quantity = 3, decimal price = 25_200m) => new
    {
        orderId,
        supplierInvoiceNumber = number,
        invoiceDate = "2026-10-20",
        lines = new[] { new { lineNo = 1, quantity, unitPriceExclVat = price } },
        statedTotalInclVat = Math.Round(quantity * price * 1.15m, 2),
        scanId,
    };

    private Task<HttpResponseMessage> CreateAsync(string token, object body) =>
        app.SendHttpAsync(InvoicingApp.Request(HttpMethod.Post, Url, token, body: body));

    [Fact]
    public async Task Invoice_with_scan_id_is_source_scan_with_document()
    {
        var tenant = InvoicingApp.NewTenant();
        var order = await app.CreateOrderAsync(tenant);
        var scanId = Guid.NewGuid();
        app.Assistant.AddScan(tenant, scanId, Scan(scanId, "INV-S-1"));

        using var response = await CreateAsync(InvoicingApp.Token(tenant), Body(order.OrderId, "INV-S-1", scanId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var invoice = (await response.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!;
        invoice.Source.ShouldBe(InvoiceSource.Scan);
        (await app.ScalarAsync<string>("SELECT source FROM invoices WHERE id = @id", ("id", invoice.Id))).ShouldBe("Scan");
        (await app.ScalarAsync<string>("SELECT document->>'id' FROM invoices WHERE id = @id", ("id", invoice.Id))).ShouldBe(scanId.ToString());
        (await app.ScalarAsync<string>("SELECT document->>'supplierVatNumber' FROM invoices WHERE id = @id", ("id", invoice.Id)))
            .ShouldBe("4123456789");

        // Read as Invoicing itself, for the caller's tenant.
        var call = app.Assistant.Calls.Last(c => c.ScanId == scanId);
        call.Tenant.ShouldBe(tenant);
        call.CallingService.ShouldBe("invoicing-api");
        call.HasBearer.ShouldBeTrue();
    }

    /// <summary>Review Focus 4: another tenant's scan (and one the Assistant wrongly hands over) is never attached; the caller gets a 422.</summary>
    [Fact]
    public async Task Other_tenant_scan_is_404_for_invoicing()
    {
        var tenant = InvoicingApp.NewTenant();
        var other = InvoicingApp.NewTenant();
        var order = await app.CreateOrderAsync(tenant);
        var othersScan = Guid.NewGuid();
        app.Assistant.AddScan(other, othersScan, Scan(othersScan, "INV-O-1"));
        var leaked = Guid.NewGuid();
        app.Assistant.AddScan(other, leaked, Scan(leaked, "INV-O-2"), leak: true);

        foreach (var (scanId, number) in new[] { (othersScan, "INV-O-1"), (Guid.NewGuid(), "INV-O-0"), (leaked, "INV-O-2") })
        {
            using var response = await CreateAsync(InvoicingApp.Token(tenant), Body(order.OrderId, number, scanId));

            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
            JsonDocument.Parse(body).RootElement.GetProperty("title").GetString().ShouldBe("The scan was not found.");
            (await app.InvoiceIdAsync(order.OrderId, number)).ShouldBeNull();
        }

        app.Assistant.Calls.Where(c => c.ScanId == othersScan).ShouldAllBe(c => c.Tenant == tenant, "always asked for the caller's tenant");
    }

    [Fact]
    public async Task Lines_come_from_body_not_scan()
    {
        var tenant = InvoicingApp.NewTenant();
        var order = await app.CreateOrderAsync(tenant);
        var scanId = Guid.NewGuid();
        app.Assistant.AddScan(tenant, scanId, Scan(scanId, "INV-S-2"));

        using var response = await CreateAsync(InvoicingApp.Token(tenant), Body(order.OrderId, "INV-S-2", scanId, quantity: 2, price: 25_200m));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var invoice = (await response.Content.ReadFromJsonAsync<InvoiceDto>(InvoicingApp.Json))!;
        var line = invoice.Lines.ShouldHaveSingleItem();
        line.LineNo.ShouldBe(1);
        line.Quantity.ShouldBe(2, "the reviewed quantity, not the scan's 9");
        line.UnitPriceExclVat.ShouldBe(25_200m, "the reviewed price, not the scan's R 1,00");
        invoice.TotalExclVat.ShouldBe(50_400m);
    }
}
