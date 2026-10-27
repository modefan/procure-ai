namespace ProcureAi.Contracts.Purchasing;

/// <summary>A quantity against an order line: received, or (when an order is closed short) the outstanding quantity.</summary>
public sealed record ReceivedLine(
    int LineNo,
    int Quantity);
