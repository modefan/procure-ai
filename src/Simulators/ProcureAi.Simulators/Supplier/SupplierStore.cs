namespace ProcureAi.Simulators.Supplier;

/// <summary>An order the supplier has received.</summary>
/// <remarks><c>SupplierCode</c>: the code the buyer gave for this supplier (quoted on its invoices), or the configured one when it gave none.</remarks>
public sealed class SupplierOrder(string id, CreateOrderRequest request, string defaultSupplierCode)
{
    public string Id { get; } = id;

    public string ExternalReference { get; } = request.ExternalReference;

    public string BuyerReference { get; } = request.BuyerReference;

    public IReadOnlyList<OrderLineDto> Lines { get; } = [.. request.Lines];

    public string SupplierCode { get; } = string.IsNullOrWhiteSpace(request.SupplierCode) ? defaultSupplierCode : request.SupplierCode.Trim();

    public volatile bool Cancelled;

    private int _timesReceived;

    /// <summary>How often the buyer sent this order (the first time plus every repeat the supplier deduplicated).</summary>
    public int TimesReceived => Volatile.Read(ref _timesReceived);

    internal void CountReceipt() => Interlocked.Increment(ref _timesReceived);
}

/// <summary>The supplier's orders, in memory. The same external reference is the same order (dedup).</summary>
public sealed class SupplierStore
{
    private const int FirstOrderNumber = 1001;

    private readonly object _gate = new();
    private readonly Dictionary<string, SupplierOrder> _byReference = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SupplierOrder> _byId = new(StringComparer.Ordinal);
    private int _orders;
    private int _invoices;

    /// <summary>The order for this external reference; <c>Created</c> is false when it was received before.</summary>
    public (SupplierOrder Order, bool Created) GetOrAdd(CreateOrderRequest request, string defaultSupplierCode)
    {
        lock (_gate)
        {
            if (_byReference.TryGetValue(request.ExternalReference, out var existing))
            {
                existing.CountReceipt();
                return (existing, false);
            }

            var order = new SupplierOrder($"SO-{FirstOrderNumber + _orders++}", request, defaultSupplierCode);
            order.CountReceipt();
            _byReference[order.ExternalReference] = order;
            _byId[order.Id] = order;
            return (order, true);
        }
    }

    public SupplierOrder? Find(string supplierOrderId)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(supplierOrderId);
        }
    }

    /// <summary>The orders whose buyer reference is exactly <paramref name="buyerReference"/>, oldest first.</summary>
    public IReadOnlyList<SupplierOrder> ForBuyerReference(string buyerReference)
    {
        lock (_gate)
        {
            return [.. _byId.Values.Where(o => string.Equals(o.BuyerReference, buyerReference, StringComparison.Ordinal)).OrderBy(o => o.Id, StringComparer.Ordinal)];
        }
    }

    public bool IsCancelled(string supplierOrderId) => Find(supplierOrderId)?.Cancelled ?? false;

    /// <summary>Forgets every order and starts the order and invoice numbers again (the nightly demo reset).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _byReference.Clear();
            _byId.Clear();
            _orders = 0;
            _invoices = 0;
        }
    }

    public string NextInvoiceNumber()
    {
        lock (_gate)
        {
            return $"INV-{FirstOrderNumber + _invoices++}";
        }
    }
}
