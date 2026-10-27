namespace ProcureAi.Contracts.Requests;

/// <summary>One line of a request as other services see it: what to buy, how many, at what price excluding VAT (decimal Rand, ZAR).</summary>
public sealed record RequestLineItem(
    int LineNo,
    string Sku,
    string Description,
    int Quantity,
    decimal UnitPriceExclVat);
