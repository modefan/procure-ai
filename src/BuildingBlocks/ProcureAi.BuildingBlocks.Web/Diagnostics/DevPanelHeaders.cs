using Microsoft.AspNetCore.Http;

namespace ProcureAi.BuildingBlocks.Web.Diagnostics;

/// <summary>
/// Header names of the dev panel and the per-request notes the filters leave for the middleware. <c>X-Trace-Id</c> is always
/// sent; the others only when <c>DevPanel:Enabled</c> is true and the request carries <c>X-Dev-Panel: 1</c>.
/// </summary>
public static class DevPanelHeaders
{
    public const string Request = "X-Dev-Panel";
    public const string TraceId = "X-Trace-Id";
    public const string Cache = "X-Cache";
    public const string Coalesced = "X-Coalesced";
    public const string Idempotent = "X-Idempotent";
    public const string Policy = "X-Policy";
    public const string Tenant = "X-Tenant";
    public const string Instance = "X-Instance";

    private const string NotesKey = "ProcureAi.DevPanel.Notes";

    /// <summary>True when the caller asked for the dev panel headers.</summary>
    public static bool IsRequested(HttpRequest request) => request.Headers[Request].ToString() == "1";

    /// <summary>Leaves a header value for the middleware to send (last write wins).</summary>
    public static void Note(HttpContext http, string header, string value) => Notes(http)[header] = value;

    /// <summary>Adds a policy name to <c>X-Policy</c>; several policies on one request are joined with a comma and a space.</summary>
    public static void NotePolicy(HttpContext http, string policy)
    {
        var notes = Notes(http);
        notes[Policy] = notes.TryGetValue(Policy, out var existing)
            ? existing.Split(", ").Contains(policy) ? existing : existing + ", " + policy
            : policy;
    }

    internal static IReadOnlyDictionary<string, string> Read(HttpContext http) =>
        http.Items[NotesKey] as Dictionary<string, string> ?? [];

    private static Dictionary<string, string> Notes(HttpContext http)
    {
        if (http.Items[NotesKey] is not Dictionary<string, string> notes)
        {
            notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            http.Items[NotesKey] = notes;
        }

        return notes;
    }

    /// <summary>The <c>X-Cache</c> value for a value served from the cache.</summary>
    public static string CacheHit(TimeSpan age) =>
        $"hit; age-ms={(long)Math.Max(0, age.TotalMilliseconds)}";
}
