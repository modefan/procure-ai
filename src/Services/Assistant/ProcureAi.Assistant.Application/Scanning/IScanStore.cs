using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Scanning;

/// <summary>
/// The stored invoice scans (MongoDB <c>invoice_scans</c>, kept <c>Assistant:Scanning:RetentionDays</c>). Every call is in the caller's
/// tenant: a scan of another tenant is not found and cannot be written.
/// </summary>
public interface IScanStore
{
    Task InsertAsync(InvoiceScan scan, CancellationToken cancellationToken);

    /// <summary>The scan of the caller's tenant, or null (unknown, expired or another tenant's).</summary>
    Task<InvoiceScan?> FindAsync(Guid id, CancellationToken cancellationToken);
}
