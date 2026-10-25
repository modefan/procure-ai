using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Net.Http.Headers;

namespace ProcureAi.BuildingBlocks.Web.Concurrency;

/// <summary>
/// The entity tag of a resource that carries a numeric version: <c>"7"</c>. Parsing accepts a strong tag (<c>"7"</c>),
/// a weak one (<c>W/"7"</c>) and a bare number, since clients and proxies differ; a list of several tags or <c>*</c> is
/// not a version we can compare, so it never parses.
/// </summary>
public static class VersionETag
{
    public static string Format(uint version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static bool TryParse(string? header, out uint version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var text = header.Trim();
        if (text.StartsWith("W/", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}

/// <summary>
/// The action changes a resource and needs <c>If-Match</c>: without the header the answer is 428 (Precondition
/// Required) as problem details. The action reads the version with <see cref="IfMatchExtensions.ExpectedVersion"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireIfMatchAttribute : Attribute, IActionFilter
{
    public const string HeaderName = "If-Match";

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.Request.Headers.ContainsKey(HeaderNames.IfMatch))
        {
            return;
        }

        const string message = "The If-Match header is required. Read the resource first and send its ETag.";
        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status428PreconditionRequired,
            Title = message,
        })
        {
            StatusCode = StatusCodes.Status428PreconditionRequired,
            ContentTypes = { "application/problem+json" },
        };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

public static class IfMatchExtensions
{
    /// <summary>
    /// The version the caller sent in <c>If-Match</c>. A value that is not one of our version tags (or a list) can never
    /// match, so it gives <c>null</c> and the action answers 412.
    /// </summary>
    public static uint? ExpectedVersion(this HttpRequest request) =>
        request.Headers.IfMatch.Count == 1 && VersionETag.TryParse(request.Headers.IfMatch[0], out var version)
            ? version
            : null;

    /// <summary>412 Precondition Failed as problem details.</summary>
    public static ObjectResult PreconditionFailed(string message) =>
        new(new ProblemDetails { Status = StatusCodes.Status412PreconditionFailed, Title = message })
        {
            StatusCode = StatusCodes.Status412PreconditionFailed,
            ContentTypes = { "application/problem+json" },
        };
}
