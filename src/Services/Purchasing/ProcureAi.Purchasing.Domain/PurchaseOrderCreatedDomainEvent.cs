namespace ProcureAi.Purchasing.Domain;

/// <summary>Raised by <see cref="PurchaseOrder.Create"/>. Application turns it into the integration event.</summary>
public sealed record PurchaseOrderCreatedDomainEvent(PurchaseOrder Order, DateTimeOffset OccurredAt);
