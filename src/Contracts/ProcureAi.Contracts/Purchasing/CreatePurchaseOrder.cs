using ProcureAi.Contracts.Requests;

namespace ProcureAi.Contracts.Purchasing;

/// <summary>Command to Purchasing: order the approved request from its supplier.</summary>
public sealed record CreatePurchaseOrder(
    Guid RequestId,
    string TenantId,
    string RequestNumber,
    string RequesterId,
    string CostCenterCode,
    string SupplierId,
    IReadOnlyList<RequestLineItem> Lines);
