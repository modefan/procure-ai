namespace ProcureAi.DemoData;

// The parts of the services' answers the seeder reads (the JSON has more). Statuses are the services' enum names.

public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record RequestView(
    Guid Id, string Number, string Title, string Status, string RequesterId, string CostCenterCode, Guid? OrderId, string? OrderNumber, uint Version);

public sealed record ChainView(Guid RequestId, string State, IReadOnlyList<StepView> Steps);

public sealed record StepView(Guid Id, int StepNo, string Level, string Status, bool CanAct, string? CannotActReason, uint Version);

public sealed record OrderView(Guid Id, Guid RequestId, string Number, string Status, IReadOnlyList<OrderLineView> Lines, uint Version);

public sealed record OrderLineView(int LineNo, string Sku, int Ordered, int Received, int ClosedShort, int Outstanding, decimal UnitPriceExclVat);

public sealed record InvoiceView(
    Guid Id, Guid OrderId, string SupplierInvoiceNumber, string Source, string Status, bool OnHold, IReadOnlyList<InvoiceLineView> Lines,
    IReadOnlyList<PaymentView> Payments, uint Version);

public sealed record InvoiceLineView(int LineNo, string Sku, int Quantity, int Paid, int? Received, int Payable);

public sealed record PaymentView(Guid Id, string Number);
