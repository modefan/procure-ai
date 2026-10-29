using Microsoft.AspNetCore.Http;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.BuildingBlocks.Tests.Web;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;

namespace ProcureAi.BuildingBlocks.Tests.Caching;

/// <summary>GET endpoints for the conditional-GET tests. The body names the caller's tenant, so it differs per caller.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/conditional")]
public sealed class ConditionalController(ICurrentUser user) : ControllerBase
{
    [HttpGet("plain")]
    public IActionResult Plain() => Ok(new { user.TenantId, Value = "plain" });

    [HttpGet("versioned")]
    public IActionResult Versioned()
    {
        Response.Headers.ETag = VersionETag.Format(7);
        return Ok(new { user.TenantId, Version = 7 });
    }

    [HttpGet("file")]
    public IActionResult Pdf() => File(System.Text.Encoding.UTF8.GetBytes("%PDF-fake"), "application/pdf");

    /// <summary>Writes one event, then waits for <see cref="EventStreamGate"/> before the second: proves the body is not held back.</summary>
    [HttpGet("events")]
    public async Task Events(CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        await Response.WriteAsync("data: one\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
        await EventStreamGate.Task.WaitAsync(cancellationToken);
        await Response.WriteAsync("data: two\n\n", cancellationToken);
    }

    public static TaskCompletionSource EventStreamGate { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [HttpGet("missing")]
    public IActionResult Missing() => NotFound();

    [HttpGet("cached")]
    [CachedGet]
    public IActionResult Cached([FromQuery] string id)
    {
        ExecutionCounter.Increment(id);
        return Ok(new { user.TenantId, Value = "cached" });
    }

    [HttpPost("create")]
    [Idempotent]
    public IActionResult Create() => Ok();

    [HttpPut("update")]
    [RequireIfMatch]
    public IActionResult Update() => Ok();

    [HttpPost("plain-post")]
    public IActionResult PlainPost() => Ok();

    [HttpGet("paged")]
    public IActionResult Paged([FromQuery] ProcureAi.BuildingBlocks.Web.Paging.PagingQuery paging) =>
        Ok(new { paging.Page, PageSize = paging.Size() });
}
