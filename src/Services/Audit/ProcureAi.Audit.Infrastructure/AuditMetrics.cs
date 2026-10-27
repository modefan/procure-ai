using System.Diagnostics.Metrics;
using ProcureAi.Audit.Application.Abstractions;

namespace ProcureAi.Audit.Infrastructure;

/// <summary>
/// The Audit service's business metrics on the OpenTelemetry meter <c>ProcureAi.Audit</c> (exported with every <c>ProcureAi.*</c>
/// meter by the service defaults). The meter comes from <see cref="IMeterFactory"/>, so each host (and test host) has its own.
/// </summary>
public sealed class AuditMetrics : IAuditMetrics
{
    public const string MeterName = "ProcureAi.Audit";
    public const string OutsideAppChanges = "procure.audit.outside_app_changes";

    private readonly Counter<long> _outsideAppChanges;

    public AuditMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _outsideAppChanges = meter.CreateCounter<long>(
            OutsideAppChanges, unit: "{change}", description: "Changes of a service database made outside the app (no interceptor row).");
    }

    public void OutsideAppChange(string database, string table) =>
        _outsideAppChanges.Add(1, new KeyValuePair<string, object?>("database", database), new KeyValuePair<string, object?>("table", table));
}
