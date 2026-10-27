using System.Diagnostics.Metrics;
using ProcureAi.Invoicing.Application.Abstractions;
using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Infrastructure;

/// <summary>
/// The Invoicing service's business metrics on the OpenTelemetry meter <c>ProcureAi.Invoicing</c> (exported with every
/// <c>ProcureAi.*</c> meter by the service defaults): invoices found not to match (by source), and failed hand-overs of payments to
/// the ERP (by reason). The ERP circuit breaker's own events are Polly's <c>resilience.polly.*</c> metrics (meter <c>Polly</c>).
/// </summary>
public sealed class InvoicingMetrics : IInvoicingMetrics
{
    public const string MeterName = "ProcureAi.Invoicing";
    public const string Mismatches = "procure.invoicing.mismatches";
    public const string ErpExportFailures = "procure.invoicing.erp_export_failures";

    private readonly Counter<long> _mismatches;
    private readonly Counter<long> _exportFailures;

    public InvoicingMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _mismatches = meter.CreateCounter<long>(
            Mismatches, unit: "{invoice}", description: "Invoices that did not match their order and the goods received, by source.");
        _exportFailures = meter.CreateCounter<long>(
            ErpExportFailures, unit: "{attempt}", description: "Attempts to hand a payment to the ERP that failed, by reason.");
    }

    public void InvoiceMismatched(InvoiceSource source) =>
        _mismatches.Add(1, new KeyValuePair<string, object?>("source", source.ToString()));

    public void ErpExportFailed(string reason) =>
        _exportFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
