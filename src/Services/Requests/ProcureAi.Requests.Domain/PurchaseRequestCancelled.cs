namespace ProcureAi.Requests.Domain;

/// <summary>Domain event: a submitted or approved request was cancelled (a draft cancels silently). Application maps it to the integration event.</summary>
public sealed record PurchaseRequestCancelled(
    Guid RequestId,
    string TenantId,
    string Number,
    string CancelledBy,
    string? Reason,
    DateTimeOffset CancelledAt);
