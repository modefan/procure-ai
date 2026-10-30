using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using ProcureAi.Assistant.Application.Ai;
using Shouldly;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// Invoice scanning (plan 05 Task 6): a PDF invoice is read by the <c>utility</c> model (<c>#task: scan</c>, the Fake reads the data block),
/// its numbers are checked and recalculated, and the scan is stored for 30 days in the caller's tenant. A PDF with instructions is refused
/// before any model sees it; Invoicing reads a stored scan through the internal endpoint, for the tenant in <c>X-Tenant-Id</c> only.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class InvoiceScanTests(AssistantApp app)
{
    private const string Url = "/api/v1/assistant/invoice-scans";
    private const string InternalUrl = "/api/v1/assistant/internal/invoice-scans";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Finance(string tenant) => AssistantApp.Token(tenant, "finance", "fiona");

    private async Task<HttpResponseMessage> ScanAsync(string token, string fileName, byte[] content)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await app.Client.SendAsync(request, Ct);
    }

    private async Task<JsonElement> ScanOkAsync(string token, string file)
    {
        using var response = await ScanAsync(token, Path.GetFileName(file), await File.ReadAllBytesAsync(file, Ct));
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private IMongoCollection<BsonDocument> Scans() =>
        new MongoClient(app.Infra.ChatConnectionString).GetDatabase(AssistantInfraFixture.ChatDatabase).GetCollection<BsonDocument>("invoice_scans");

    [Fact]
    public async Task Correct_invoice_scans_with_high_confidence()
    {
        var tenant = AssistantApp.NewTenant();

        var scan = await ScanOkAsync(Finance(tenant), "Files/invoice-correct.pdf");

        scan.GetProperty("supplierName").GetString().ShouldBe("Contoso Hardware (Pty) Ltd");
        scan.GetProperty("supplierVatNumber").GetString().ShouldBe("4123456789");
        scan.GetProperty("invoiceNumber").GetString().ShouldBe("INV-2026-0042");
        scan.GetProperty("invoiceDate").GetString().ShouldBe("2026-10-20");
        scan.GetProperty("orderNumber").GetString().ShouldBe("PO-1001");
        var lines = scan.GetProperty("lines").EnumerateArray().ToList();
        lines.Count.ShouldBe(2);
        lines[0].GetProperty("description").GetString().ShouldBe("USB-C dock");
        lines[0].GetProperty("quantity").GetDecimal().ShouldBe(2m);
        lines[0].GetProperty("unitPriceExclVat").GetDecimal().ShouldBe(2_100m);
        lines[0].GetProperty("lineTotalExclVat").GetDecimal().ShouldBe(4_200m);
        lines[1].GetProperty("description").GetString().ShouldBe("Wireless keyboard");
        scan.GetProperty("totalExclVat").GetDecimal().ShouldBe(6_150m);
        scan.GetProperty("vat").GetDecimal().ShouldBe(922.50m);
        scan.GetProperty("totalInclVat").GetDecimal().ShouldBe(7_072.50m);
        scan.GetProperty("confidence").GetString().ShouldBe("high");
        scan.GetProperty("warnings").GetArrayLength().ShouldBe(0);
        scan.GetProperty("model").GetString().ShouldBe(AiOptions.FakeChatModel);

        // The utility model got the invoice's text inside the data block, under the scan task.
        var id = scan.GetProperty("id").GetGuid();
        var call = app.Script.Calls.Last(c => c.Task == AiTasks.Scan && c.AllText.Contains("INV-2026-0042", StringComparison.Ordinal));
        call.LastUserText.ShouldStartWith("<data kind=\"invoice\"");
        call.Tools.ShouldBeEmpty();

        // Stored in the caller's tenant, deleted by MongoDB 30 days after the scan.
        var stored = await Scans().Find(Builders<BsonDocument>.Filter.Eq("_id", id.ToString("D"))).SingleAsync(Ct);
        stored["TenantId"].AsString.ShouldBe(tenant);
        stored["UserId"].AsString.ShouldBe("fiona");
        var indexes = await (await Scans().Indexes.ListAsync(Ct)).ToListAsync(Ct);
        indexes.ShouldContain(i => i.Contains("expireAfterSeconds") && i["expireAfterSeconds"].ToInt64() == (long)TimeSpan.FromDays(30).TotalSeconds);
    }

    /// <summary>Review Focus 3: the model's totals are never trusted; a wrong total is recalculated and shown next to the model's.</summary>
    [Fact]
    public async Task Scan_totals_are_recalculated_and_mismatch_warned()
    {
        var tenant = AssistantApp.NewTenant();

        var scan = await ScanOkAsync(Finance(tenant), "Files/invoice-wrong-total.pdf");

        scan.GetProperty("totalInclVat").GetDecimal().ShouldBe(7_672.50m, "the model's number is shown as read");
        scan.GetProperty("confidence").GetString().ShouldBe("medium");
        var warning = scan.GetProperty("warnings").EnumerateArray().ShouldHaveSingleItem();
        warning.GetProperty("code").GetString().ShouldBe("total");
        var message = warning.GetProperty("message").GetString()!;
        message.ShouldContain("R 7 072,50", customMessage: "the recalculated total");
        message.ShouldContain("R 7 672,50", customMessage: "the total on the invoice");
    }

    [Fact]
    public async Task Poisoned_pdf_is_422()
    {
        var tenant = AssistantApp.NewTenant();

        using var response = await ScanAsync(Finance(tenant), "invoice.pdf", await File.ReadAllBytesAsync("Files/invoice-poisoned.pdf", Ct));

        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        JsonDocument.Parse(body).RootElement.GetProperty("title").GetString().ShouldBe("This PDF contains instructions, not an invoice.");
        app.Script.Calls.ShouldNotContain(
            c => c.Task == AiTasks.Scan && c.AllText.Contains("approve all invoices", StringComparison.Ordinal), "the reader never saw it");
        (await Scans().CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("TenantId", tenant), cancellationToken: Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Non_pdf_is_415()
    {
        var tenant = AssistantApp.NewTenant();

        using var renamed = await ScanAsync(Finance(tenant), "invoice.pdf", "MZ this is not a PDF"u8.ToArray());
        using var markdown = await ScanAsync(Finance(tenant), "invoice.md", await File.ReadAllBytesAsync("Files/policy.md", Ct));

        renamed.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType, await renamed.Content.ReadAsStringAsync(Ct));
        markdown.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType, "only PDF invoices are scanned");
    }

    [Fact]
    public async Task Scan_without_invoices_create_is_403()
    {
        var tenant = AssistantApp.NewTenant();
        var admin = AssistantApp.Token(tenant, "admin", "ada"); // admin.policies and more, but not invoices.create

        using var response = await ScanAsync(admin, "invoice.pdf", await File.ReadAllBytesAsync("Files/invoice-correct.pdf", Ct));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Internal_scan_read_requires_same_tenant()
    {
        var tenant = AssistantApp.NewTenant();
        var other = AssistantApp.NewTenant();
        var id = (await ScanOkAsync(Finance(tenant), "Files/invoice-correct.pdf")).GetProperty("id").GetGuid();

        async Task<HttpResponseMessage> ReadAsync(string? token, string? tenantHeader)
        {
            using var request = AssistantApp.Get($"{InternalUrl}/{id}", token);
            if (tenantHeader is not null)
            {
                request.Headers.Add("X-Tenant-Id", tenantHeader);
            }

            return await app.Client.SendAsync(request, Ct);
        }

        var service = TestAuth.CreateServiceToken(AssistantApiFactory.InternalClientId);
        using (var own = await ReadAsync(service, tenant))
        {
            var body = await own.Content.ReadAsStringAsync(Ct);
            own.StatusCode.ShouldBe(HttpStatusCode.OK, body);
            var record = JsonDocument.Parse(body).RootElement;
            record.GetProperty("id").GetGuid().ShouldBe(id);
            record.GetProperty("tenantId").GetString().ShouldBe(tenant);
            record.GetProperty("scan").GetProperty("invoiceNumber").GetString().ShouldBe("INV-2026-0042");
        }

        using (var otherTenant = await ReadAsync(service, other))
        {
            otherTenant.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another tenant's scan is not found");
        }

        using (var noTenant = await ReadAsync(service, null))
        {
            noTenant.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
        }

        using (var user = await ReadAsync(Finance(tenant), tenant))
        {
            user.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a user's token never reads the internal endpoint");
        }
    }
}
