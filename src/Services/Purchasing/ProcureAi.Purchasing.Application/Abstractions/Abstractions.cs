using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ProcureAi.Purchasing.Domain;

namespace ProcureAi.Purchasing.Application.Abstractions;

/// <summary>What the handlers need from persistence. Tenant filtering is applied by the implementation.</summary>
public interface IPurchasingDbContext
{
    /// <summary>The orders (and tombstones), with their lines and receipts loaded when asked for with Include.</summary>
    DbSet<PurchaseOrder> Orders { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The anti-corruption layer to the supplier: our order model in, the supplier wire format hidden in Infrastructure. Calls are
/// idempotent per order ID on the supplier side, so repeating one after an uncertain failure is safe.
/// </summary>
public interface ISupplierGateway
{
    /// <summary>Hands the order to the supplier. Throws <see cref="SupplierUnavailableException"/> (retry later) or <see cref="SupplierRejectedException"/> (the supplier refused it for good).</summary>
    Task<SupplierAcceptance> SubmitOrderAsync(SupplierOrder order, CancellationToken cancellationToken);

    /// <summary>Tells the supplier the order is cancelled. Same exceptions as <see cref="SubmitOrderAsync"/>.</summary>
    Task CancelOrderAsync(string supplierReference, CancellationToken cancellationToken);
}

/// <summary>An order as sent to the supplier. <c>SupplierId</c> is the catalog code of the supplier the order is with; the supplier quotes it on its invoices.</summary>
public sealed record SupplierOrder(Guid OrderId, string TenantId, string OrderNumber, string SupplierId, IReadOnlyList<OrderLine> Lines);

/// <summary>The supplier accepted the order and gave it this reference. Confirmation follows later by webhook.</summary>
public sealed record SupplierAcceptance(string SupplierReference);

/// <summary>The supplier could not be reached or answered with a server error (timeout, 5xx, open circuit). Worth retrying.</summary>
/// <param name="reason">A short metric-friendly cause: <c>timeout</c>, <c>circuit_open</c>, <c>unreachable</c> or <c>status_&lt;code&gt;</c>.</param>
public sealed class SupplierUnavailableException(string message, string reason, Exception? inner = null) : Exception(message, inner)
{
    public string Reason { get; } = reason;
}

/// <summary>The supplier refused the request for good (a 400 answer). Retrying cannot help.</summary>
public sealed class SupplierRejectedException(string message) : Exception(message);

/// <summary>The business metrics of Purchasing (meter <c>ProcureAi.Purchasing</c>), implemented in Infrastructure.</summary>
public interface IPurchasingMetrics
{
    /// <summary>A send to the supplier failed; <paramref name="reason"/> is the cause (<c>rejected</c>, <c>timeout</c>, <c>circuit_open</c>, ...).</summary>
    void SupplierSendFailed(string reason);
}
