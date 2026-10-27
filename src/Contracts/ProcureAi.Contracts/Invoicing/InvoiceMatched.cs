namespace ProcureAi.Contracts.Invoicing;

/// <summary>Integration event: the invoice matches the order and the goods received (three-way match).</summary>
public sealed record InvoiceMatched(
    Guid InvoiceId,
    Guid OrderId,
    Guid RequestId,
    string TenantId,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
