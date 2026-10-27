using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Application.Abstractions;

/// <summary>What the handlers need from persistence. Tenant filtering is applied by the implementation.</summary>
public interface IInvoicingDbContext
{
    /// <summary>Invoicing's copies of the purchase orders, with their lines loaded when asked for with Include.</summary>
    DbSet<OrderView> Orders { get; }

    /// <summary>The invoices, with their lines and payments loaded when asked for with Include.</summary>
    DbSet<Invoice> Invoices { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>The anti-corruption layer to the ERP: our payment in, the ERP wire format hidden in Infrastructure. Idempotent by payment ID on the ERP side.</summary>
public interface IErpGateway
{
    /// <summary>Hands the payment to the ERP and returns its document number. Throws <see cref="ErpUnavailableException"/> (retry later) or <see cref="ErpRejectedException"/> (refused for good).</summary>
    Task<string> ExportPaymentAsync(ErpPayment payment, CancellationToken cancellationToken);
}

/// <summary>A payment as sent to the ERP. <c>PaymentId</c> is the idempotency reference.</summary>
public sealed record ErpPayment(
    Guid PaymentId,
    string PaymentNumber,
    string SupplierId,
    string SupplierInvoiceNumber,
    Money TotalInclVat,
    IReadOnlyList<ErpPaymentLine> Lines);

/// <summary>One line of an <see cref="ErpPayment"/>, at the order price excluding VAT.</summary>
public sealed record ErpPaymentLine(int LineNo, string Sku, int Quantity, Money AmountExclVat);

/// <summary>The ERP could not be reached or answered with a server error (timeout, 5xx, open circuit). Worth retrying.</summary>
public sealed class ErpUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The ERP refused the payment for good (a 400 answer). Retrying cannot help.</summary>
public sealed class ErpRejectedException(string message) : Exception(message);

/// <summary>
/// An event about an order arrived before Invoicing has its copy of the order (<c>PurchaseOrderCreated</c> is on its way). The
/// message must be retried later; the Wolverine policy of Infrastructure schedules that.
/// </summary>
public sealed class OrderViewMissingException(Guid orderId)
    : Exception($"Invoicing does not have order {orderId} yet.")
{
    public Guid OrderId { get; } = orderId;
}

/// <summary>The business metrics of Invoicing (meter <c>ProcureAi.Invoicing</c>), implemented in Infrastructure.</summary>
public interface IInvoicingMetrics
{
    /// <summary>An invoice was found not to match (first match, or a match that turned into a mismatch).</summary>
    void InvoiceMismatched(InvoiceSource source);

    /// <summary>Handing a payment to the ERP failed (<c>unavailable</c>: retried later; <c>rejected</c>: refused for good).</summary>
    void ErpExportFailed(string reason);
}
