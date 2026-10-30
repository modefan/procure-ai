namespace ProcureAi.Assistant.Domain;

/// <summary>
/// One line as the model read it from an invoice. The quantity is a decimal on purpose: a fractional quantity is what the reader saw, and
/// the validator warns about it. Nothing here is trusted until the user has reviewed it.
/// </summary>
public sealed record ScannedLine(string Description, decimal Quantity, decimal UnitPriceExclVat, decimal LineTotalExclVat);
