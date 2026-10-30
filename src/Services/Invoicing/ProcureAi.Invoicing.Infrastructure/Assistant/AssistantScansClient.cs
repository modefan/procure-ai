using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.Invoicing.Application.Abstractions;

namespace ProcureAi.Invoicing.Infrastructure.Assistant;

/// <summary>The <c>AssistantClient</c> section: retries and timeouts of the scan read (it sits in front of finance's save).</summary>
public sealed class AssistantClientOptions : ServiceClientResilienceOptions
{
    public const string SectionName = "AssistantClient";
}

/// <summary>
/// Reads a stored invoice scan from the Assistant (<c>GET api/v1/assistant/internal/invoice-scans/{id}</c>) as this service, with its
/// client-credentials token and the tenant in <c>X-Tenant-Id</c> (the Assistant's internal policy: a service account, never a user). A 404
/// is "no such scan for this tenant". A call that fails (unreachable, timeout, open circuit, 5xx) throws the
/// <see cref="HttpRequestException"/> or Polly rejection, answered 503 with <c>Retry-After</c>; any other answer is a fault of ours
/// (a contract or configuration mismatch) and throws <see cref="InvalidOperationException"/> (500).
/// </summary>
public sealed class AssistantScansClient(HttpClient http) : IAssistantScans
{
    /// <summary>The service name of the Assistant: its address (<c>http://assistant-api</c>) through service discovery.</summary>
    public const string AssistantServiceName = "assistant-api";

    private const string ScansPath = "api/v1/assistant/internal/invoice-scans";

    public async Task<AssistantScan?> GetAsync(string tenantId, Guid scanId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ScansPath}/{scanId:D}");
        // The caller's tenant, named explicitly: the Assistant only finds a scan of this tenant.
        request.Options.Set(ServiceTokenHandler.TenantOption, tenantId);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (DependencyFailures.IsTransientStatus(response.StatusCode))
        {
            response.EnsureSuccessStatusCode();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The Assistant answered {(int)response.StatusCode} to the read of invoice scan {scanId}.");
        }

        var record = await response.Content.ReadFromJsonAsync<ScanRecord>(cancellationToken)
            ?? throw new InvalidOperationException($"The Assistant returned an empty invoice scan {scanId}.");
        if (record.Scan.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(record.TenantId))
        {
            throw new InvalidOperationException($"The Assistant returned invoice scan {scanId} without its tenant or scan.");
        }

        return new AssistantScan(record.Id, record.TenantId, record.Scan.GetRawText());
    }

    /// <summary>The part of the Assistant's <c>InvoiceScanRecordDto</c> Invoicing keeps: the scan stays raw JSON.</summary>
    private sealed record ScanRecord(Guid Id, string TenantId, JsonElement Scan);
}
