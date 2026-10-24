using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace ProcureAi.BuildingBlocks.Web.Logging;

/// <summary>Masks the values of secret headers when a header collection is destructured into a log event.</summary>
public sealed class HeaderMaskingPolicy : IDestructuringPolicy
{
    public const string Mask = "***";

    private static readonly HashSet<string> Sensitive =
        new(["X-Api-Key", "Authorization", "Cookie", "Set-Cookie", "Proxy-Authorization"], StringComparer.OrdinalIgnoreCase);

    public bool TryDestructure(
        object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
    {
        if (value is not IHeaderDictionary headers)
        {
            result = null!;
            return false;
        }

        result = new DictionaryValue(headers.Select(header => KeyValuePair.Create(
            new ScalarValue(header.Key),
            (LogEventPropertyValue)new ScalarValue(Sensitive.Contains(header.Key) ? Mask : header.Value.ToString()))));
        return true;
    }
}
