namespace ProcureAi.Contracts.Purchasing;

/// <summary>One line of a purchase order (decimal Rand, ZAR).</summary>
public sealed record OrderLineItem(
    int LineNo,
    string Sku,
    string Description,
    int Quantity,
    decimal UnitPriceExclVat);
