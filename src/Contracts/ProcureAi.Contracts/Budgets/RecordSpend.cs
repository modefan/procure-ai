namespace ProcureAi.Contracts.Budgets;

/// <summary>Command to Budgets: turn part of the reservation of a request into spend for a payment (decimal Rand, ZAR).</summary>
public sealed record RecordSpend(
    Guid RequestId,
    string TenantId,
    Guid PaymentId,
    decimal Amount);
