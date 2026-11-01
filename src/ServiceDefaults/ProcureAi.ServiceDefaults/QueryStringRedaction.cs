namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Hides the values of a request's query string in what leaves the process (plan 07 Task 0a, plan 06 carry-over 18): a query may carry a
/// credential, such as the single-use ticket of a tenant switch (<c>/bff/switch-tenant/challenge?ticket=…</c>). Every value becomes
/// <c>***</c> and the names stay, so <c>?ticket=abc&amp;page=2</c> reads <c>?ticket=***&amp;page=***</c>: a log or trace still shows which
/// parameters a request had. All values, not a list of known secret names, so a new parameter can never leak by being forgotten. Used for
/// the server span's <c>url.query</c> (here) and the request log's <c>QueryString</c> (BuildingBlocks' Serilog setup).
/// </summary>
public static class QueryStringRedaction
{
    public const string Mask = "***";

    /// <summary>The query with every value masked; a leading <c>?</c> is kept. Null or empty stays as it is.</summary>
    public static string? Redact(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return query;
        }

        var prefix = query[0] == '?' ? "?" : "";
        var parts = query[prefix.Length..].Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var equals = parts[i].IndexOf('=', StringComparison.Ordinal);
            // A part without '=' is a bare value as far as anyone can tell (?abc123): masked whole.
            parts[i] = equals < 0 ? (parts[i].Length == 0 ? "" : Mask) : string.Concat(parts[i].AsSpan(0, equals + 1), Mask);
        }

        return prefix + string.Join('&', parts);
    }
}
