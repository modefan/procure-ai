namespace ProcureAi.Simulators.Erp;

/// <summary>The payments the ERP has posted, in memory. A payment reference posts once; posting it again returns the same document.</summary>
public sealed class ErpStore
{
    private const int FirstDocumentNumber = 5000;

    private readonly object _gate = new();
    private readonly Dictionary<string, PaymentDocument> _byReference = new(StringComparer.Ordinal);
    private int _documents;

    public (PaymentDocument Document, bool Created) GetOrAdd(CreatePaymentRequest request, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_byReference.TryGetValue(request.PaymentReference, out var existing))
            {
                return (existing, false);
            }

            var document = new PaymentDocument(
                $"ERP-{FirstDocumentNumber + _documents++}", request.PaymentReference, request.VendorCode, request.InvoiceNumber,
                request.Currency, request.GrossAmountCents, request.Lines ?? [], now);
            _byReference[request.PaymentReference] = document;
            return (document, true);
        }
    }

    /// <summary>Forgets every posting and starts the document numbers again (the nightly demo reset).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _byReference.Clear();
            _documents = 0;
        }
    }

    public PaymentDocument? Find(string paymentReference)
    {
        lock (_gate)
        {
            return _byReference.GetValueOrDefault(paymentReference);
        }
    }
}
