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
