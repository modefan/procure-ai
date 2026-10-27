using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ProcureAi.Invoicing.Application.Commands;
using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Api.Controllers;

/// <summary>One line of an invoice typed in by finance: the order line it is for, how many, at which unit price excluding VAT.</summary>
public sealed record InvoiceLineBody(
    [Range(1, InvoiceLimits.MaxLines)] int LineNo,
    [Range(1, InvoiceLimits.MaxQuantity)] int Quantity,
    [Range(typeof(decimal), "0", InvoiceLimits.MaxAmount)] decimal UnitPriceExclVat);

/// <summary>An invoice typed in by finance (source Manual) against an order the caller may see.</summary>
public sealed record CreateInvoiceBody(
    [Required] Guid OrderId,
    [Required, StringLength(InvoiceLimits.InvoiceNumberLength, MinimumLength = 1)] string SupplierInvoiceNumber,
    [Required] DateOnly InvoiceDate,
    [Required, MinLength(1), MaxLength(InvoiceLimits.MaxLines)] IReadOnlyList<InvoiceLineBody> Lines,
    [Range(typeof(decimal), "0", InvoiceLimits.MaxAmount)] decimal StatedTotalInclVat)
{
    internal RecordInvoice ToCommand() => new(
        OrderId, SupplierInvoiceNumber.Trim(), InvoiceDate,
        [.. Lines.Select(l => new InvoiceLineInput(l.LineNo, l.Quantity, l.UnitPriceExclVat))],
        StatedTotalInclVat, InvoiceSource.Manual, JsonSerializer.Serialize(this, InvoiceLimits.Json), SupplierId: null);
}

/// <summary>How many of one invoice line to pay.</summary>
public sealed record PayLineBody(
    [Range(1, InvoiceLimits.MaxLines)] int LineNo,
    [Range(1, InvoiceLimits.MaxQuantity)] int Quantity);

/// <summary>A payment: no lines (or no body) pays everything payable; lines pay the chosen quantities.</summary>
public sealed record ReleasePaymentBody([MaxLength(InvoiceLimits.MaxLines)] IReadOnlyList<PayLineBody>? Lines)
{
    internal ReleasePayment ToCommand(Guid invoiceId, uint expectedVersion) =>
        new(invoiceId, expectedVersion, Lines is { Count: > 0 } ? [.. Lines.Select(l => new PayLineInput(l.LineNo, l.Quantity))] : null);
}

/// <summary>Why finance holds the invoice.</summary>
public sealed record HoldInvoiceBody(
    [Required, StringLength(InvoiceLimits.ReasonLength, MinimumLength = InvoiceLimits.MinReasonLength)] string Reason)
{
    internal HoldInvoice ToCommand(Guid invoiceId, uint expectedVersion) => new(invoiceId, expectedVersion, Reason.Trim());
}

/// <summary>Input size limits of the API (the domain enforces the business rules).</summary>
public static class InvoiceLimits
{
    public const int MaxLines = 100;
    public const int MaxQuantity = 100_000;
    public const int MinReasonLength = 3;

    /// <summary>The sizes of the invoice columns the API and the webhook write to (as in Infrastructure's model).</summary>
    public const int InvoiceNumberLength = 64;
    public const int ReasonLength = 500;
    public const int SupplierLength = 100;
    public const int SkuLength = 100;

    /// <summary>R 1 000 000 000,00: far above any demo amount, and within numeric(18,2).</summary>
    public const string MaxAmount = "1000000000";

    /// <summary><see cref="MaxAmount"/> in cents, for the supplier's webhook.</summary>
    public const long MaxCents = 100_000_000_000;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
