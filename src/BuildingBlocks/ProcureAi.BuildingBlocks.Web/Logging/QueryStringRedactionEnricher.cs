using Microsoft.Extensions.Hosting;
using Serilog.Core;
using Serilog.Events;

namespace ProcureAi.BuildingBlocks.Web.Logging;

/// <summary>
/// Masks the query values in ASP.NET Core's request log (<c>Request starting</c> / <c>Request finished</c>, property <c>QueryString</c>),
/// the same way as the traces (<see cref="QueryStringRedaction"/>): a query may carry a credential such as the tenant switch ticket. The
/// rendered message is built from the property, so it shows <c>?ticket=***</c> too.
/// </summary>
internal sealed class QueryStringRedactionEnricher : ILogEventEnricher
{
    private const string Property = "QueryString";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue(Property, out var value) && value is ScalarValue { Value: string { Length: > 0 } query })
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(Property, new ScalarValue(QueryStringRedaction.Redact(query))));
        }
    }
}
