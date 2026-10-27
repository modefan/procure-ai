namespace ProcureAi.Contracts.Invoicing;

/// <summary>Integration event: the invoice does not match; <c>Reasons</c> says why.</summary>
public sealed record InvoiceMismatched(
    Guid InvoiceId,
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    IReadOnlyList<string> Reasons,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
