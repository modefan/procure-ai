using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// The browser's log sink. The Angular <c>Logger</c> posts batches of warn and error entries here; each entry is written through
/// Serilog (and so to OpenTelemetry and the Aspire dashboard) with source <c>browser</c>, the user and tenant of the session if
/// there is one (IDs only), and the browser's trace ID if it sent one.
/// Anonymous and XSRF-exempt: <c>sendBeacon</c> cannot set headers, and logs may come before the user signs in. A session is
/// rate-limited (<c>Gateway:ClientLogs:PermitPerMinute</c>), on top of the gateway's per-IP limit.
/// </summary>
public static partial class ClientLogEndpoints
{
    public const string Path = "/api/client-logs";

    /// <summary>A batch is at most a few dozen small entries; anything bigger is not from our Logger.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    private const int MaxIdLength = 64;
    private const string Mask = "***";
    private const int MaxDepth = 8;

    public static IEndpointRouteBuilder MapClientLogs(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, (Delegate)AcceptAsync).AllowAnonymous().WithOrder(5);
        return app;
    }

    private static async Task<IResult> AcceptAsync(
        HttpContext http, ClientLogRateLimiter limiter, IOptions<ClientLogOptions> options, ILoggerFactory loggers)
    {
        if (http.Request.ContentLength > MaxBodyBytes)
        {
            return TooLarge();
        }

        // Also caps a chunked body that declares no length. Some hosts (the in-memory test server) make the limit read-only.
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        byte[] body;
        try
        {
            body = await ReadBodyAsync(http);
        }
        catch (BadHttpRequestException)
        {
            return TooLarge();
        }

        if (body.Length > MaxBodyBytes)
        {
            return TooLarge();
        }

        // The session is the verified cookie ticket, never a header or a cookie value the client could make up.
        var session = (await http.AuthenticateAsync(BffSchemes.Cookie)).Principal;
        var userId = SafeId(session?.FindFirst("sub")?.Value);
        var tenantId = SafeId(session?.FindFirst("tenant")?.Value);
        var partition = userId is null ? $"ip:{http.Connection.RemoteIpAddress}" : $"user:{userId}";
        if (!limiter.TryAcquire(partition, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many log batches");
        }

        JsonDocument batch;
        try
        {
            batch = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not a JSON log batch");
        }

        using (batch)
        {
            if (batch.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A log batch is a JSON array");
            }

            var logger = loggers.CreateLogger("Browser");
            var maxLength = options.Value.MaxMessageLength;
            foreach (var entry in batch.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    Write(logger, entry, userId, tenantId, maxLength);
                }
            }
        }

        return Results.Accepted();
    }

    private static void Write(ILogger logger, JsonElement entry, string? userId, string? tenantId, int maxLength)
    {
        var source = Clean(Text(entry, "source"), maxLength) is { Length: > 0 } s ? s : "unknown";
        var message = Clean(Text(entry, "message"), maxLength);
        var properties = new Dictionary<string, object?>
        {
            ["Source"] = "browser",
            ["ClientSource"] = source,
            ["ClientMessage"] = message,
        };

        AddIf(properties, "UserId", userId);
        AddIf(properties, "TenantId", tenantId);
        if (Text(entry, "traceId") is { } traceId && TraceIdPattern().IsMatch(traceId))
        {
            properties["TraceId"] = traceId;
        }

        if (Text(entry, "url") is { } url)
        {
            // A path only: a query string may hold personal data.
            AddIf(properties, "ClientUrl", Clean(url.Split('?', '#')[0], maxLength));
        }

        if (Text(entry, "timestamp") is { } timestamp)
        {
            AddIf(properties, "ClientTimestamp", Clean(timestamp, 40));
        }

        if (entry.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Undefined)
        {
            AddIf(properties, "ClientData", Clean(MaskSecrets(data).ToJsonString(), maxLength));
        }

        using (logger.BeginScope(properties))
        {
            logger.Log(MapLevel(Text(entry, "level")), "Browser {ClientSource}: {ClientMessage}", source, message);
        }
    }

    private static LogLevel MapLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Information,
        "error" => LogLevel.Error,
        _ => LogLevel.Warning,
    };

    private static void AddIf(Dictionary<string, object?> properties, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            properties[name] = value;
        }
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? SafeId(string? value) =>
        value is { Length: > 0 and <= MaxIdLength } && IdPattern().IsMatch(value) ? value : null;

    /// <summary>Control characters (line breaks that would fake a log line) become spaces; the text is cut at the limit.</summary>
    private static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cut = text.Length > maxLength ? text[..maxLength] : text;
        var builder = new StringBuilder(cut.Length);
        foreach (var c in cut)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }

    /// <summary>The Angular logger already masks these keys; a client that is not our logger gets the same treatment here.</summary>
    private static JsonNode MaskSecrets(JsonElement element, int depth = 0)
    {
        if (depth >= MaxDepth)
        {
            return JsonValue.Create("[depth limit]");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    result[property.Name] = SecretKeyPattern().IsMatch(property.Name) ? JsonValue.Create(Mask) : MaskSecrets(property.Value, depth + 1);
                }

                return result;
            case JsonValueKind.Array:
                var list = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(MaskSecrets(item, depth + 1));
                }

                return list;
            default:
                return JsonNode.Parse(element.GetRawText())!;
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext http)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > MaxBodyBytes)
            {
                break;
            }
        }

        return memory.ToArray();
    }

    private static IResult TooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Log batch too large");

    [GeneratedRegex("token|password|key|secret|authorization", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+\\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{32}\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TraceIdPattern();
}
