using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.Requests.Application.Abstractions;

namespace ProcureAi.Requests.Infrastructure;

/// <summary>The <c>ApprovalsClient</c> section: retries and timeouts of the call to Approvals' preview (it sits in front of a submission).</summary>
public sealed class ApprovalsClientOptions : ServiceClientResilienceOptions
{
    public const string SectionName = "ApprovalsClient";
}

/// <summary>
/// Asks the Approvals service (<c>GET api/v1/approvals/internal/preview</c>, as this service with its client-credentials token,
/// on behalf of the caller) whether a request could be approved. Not cached here: Approvals caches its answer for a second, and a
/// submission must see the organisation as it is now.
/// <para>
/// A call that fails (Approvals unreachable, timeout, open circuit, or a 5xx such as Approvals' own 503 when Access is down) throws
/// the <see cref="HttpRequestException"/> or Polly rejection, which the API answers with 503 and <c>Retry-After</c>. Any other
/// non-success answer is a fault of ours (a contract or configuration mismatch) and throws <see cref="InvalidOperationException"/> (500).
/// </para>
/// </summary>
public sealed class ApprovalPreviewClient(HttpClient http) : IApprovalPreview
{
    /// <summary>The service name of Approvals: its address (<c>http://approvals-api</c>) through service discovery.</summary>
    public const string ApprovalsServiceName = "approvals-api";

    private const string PreviewPath = "api/v1/approvals/internal/preview";

    public async Task<ApprovalPreviewResult> CheckAsync(
        string tenantId, string costCenterCode, decimal totalInclVat, string requesterId, CancellationToken cancellationToken)
    {
        var url = $"{PreviewPath}?costCenter={Uri.EscapeDataString(costCenterCode)}"
                  + $"&amount={totalInclVat.ToString(CultureInfo.InvariantCulture)}"
                  + $"&requesterId={Uri.EscapeDataString(requesterId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // The request's tenant, whoever the caller is.
        request.Options.Set(ServiceTokenHandler.TenantOption, tenantId);
        using var response = await http.SendAsync(request, cancellationToken);
        if (DependencyFailures.IsTransientStatus(response.StatusCode))
        {
            response.EnsureSuccessStatusCode();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Approvals answered {(int)response.StatusCode} to the preview of a request on '{costCenterCode}'.");
        }

        var preview = await response.Content.ReadFromJsonAsync<PreviewAnswer>(cancellationToken)
            ?? throw new InvalidOperationException("Approvals returned an empty preview.");
        return new ApprovalPreviewResult(preview.Possible, preview.Reason);
    }

    /// <summary>The part of Approvals' <c>ApprovalPreviewDto</c> this check needs (its steps are for the preview screen).</summary>
    private sealed record PreviewAnswer(bool Possible, string? Reason);
}
