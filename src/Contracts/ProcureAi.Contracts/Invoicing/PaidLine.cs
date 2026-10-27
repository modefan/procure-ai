namespace ProcureAi.Contracts.Invoicing;

/// <summary>The quantity and amount (excluding VAT, decimal Rand) a payment covers on one order line.</summary>
public sealed record PaidLine(
    int LineNo,
    int Quantity,
    decimal AmountExclVat);
