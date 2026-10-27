using System.Diagnostics.Metrics;

namespace ProcureAi.BuildingBlocks.Web.Webhooks;

/// <summary>Counts refused webhooks (meter <c>ProcureAi.Webhooks</c>, exported by the service defaults).</summary>
public sealed class WebhookMetrics
{
    public const string MeterName = "ProcureAi.Webhooks";
    public const string RejectedCounter = "procure.webhooks.rejected";

    private readonly Counter<long> _rejected;

    public WebhookMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _rejected = meter.CreateCounter<long>(RejectedCounter, description: "Webhooks refused before reaching the action");
    }

    public void Rejected(string source, string reason) =>
        _rejected.Add(1, new KeyValuePair<string, object?>("source", source), new KeyValuePair<string, object?>("reason", reason));
}
