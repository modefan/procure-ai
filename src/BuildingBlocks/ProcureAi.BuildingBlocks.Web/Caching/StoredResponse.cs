using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>A captured HTTP response, kept in the cache and replayed to later or waiting callers.</summary>
public sealed record StoredResponse(
    int StatusCode,
    string? ContentType,
    string? Location,
    string? ETag,
    byte[] Body,
    DateTimeOffset CreatedUtc,
    string? RequestHash = null)
{
    [JsonIgnore]
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>What a caller gets when the work failed; never exposes the exception.</summary>
    public static StoredResponse InternalError(string? requestHash = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
        });
        return new StoredResponse(
            StatusCodes.Status500InternalServerError, "application/problem+json", null, null, body,
            DateTimeOffset.UtcNow, requestHash);
    }

    public IActionResult ToResult() => new StoredResult(this);

    private sealed class StoredResult(StoredResponse stored) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = stored.StatusCode;
            if (stored.ContentType is not null)
            {
                response.ContentType = stored.ContentType;
            }

            if (stored.Location is not null)
            {
                response.Headers.Location = stored.Location;
            }

            if (stored.ETag is not null)
            {
                response.Headers.ETag = stored.ETag;
            }

            response.ContentLength = stored.Body.Length;
            await response.Body.WriteAsync(stored.Body, context.HttpContext.RequestAborted);
        }
    }
}
