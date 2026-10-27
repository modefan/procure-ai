namespace ProcureAi.Contracts.Approvals;

/// <summary>Integration event (saga): the purchase order for the request exists.</summary>
public sealed record RequestOrdered(
    Guid RequestId,
    string TenantId,
    string Number,
    Guid OrderId,
    string OrderNumber,
    DateTimeOffset OccurredAt) : IIntegrationEvent;
