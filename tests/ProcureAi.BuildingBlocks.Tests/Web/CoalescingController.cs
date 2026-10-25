using System.Collections.Concurrent;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>Counts how often each action really runs; shared by all test hosts in the process.</summary>
public static class ExecutionCounter
{
    private static readonly ConcurrentDictionary<string, int> Counts = new();

    public static int Increment(string id) => Counts.AddOrUpdate(id, 1, (_, n) => n + 1);

    public static int Get(string id) => Counts.GetValueOrDefault(id);
}

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/coalesce")]
public sealed class CoalescingController(ICurrentUser user) : ControllerBase
{
    [HttpGet("get")]
    [CachedGet]
    public async Task<IActionResult> Get([FromQuery] string id, [FromQuery] int delayMs = 300)
    {
        var n = ExecutionCounter.Increment(id);
        await Task.Delay(delayMs);
        return Ok(new { n, user.TenantId, At = Guid.NewGuid() });
    }

    [HttpGet("broken")]
    [CachedGet]
    public IActionResult Broken([FromQuery] string id)
    {
        var n = ExecutionCounter.Increment(id);
        return n == 1 ? StatusCode(503) : Ok(new { n });
    }

    [HttpGet("fail-slow")]
    [CachedGet]
    public async Task<IActionResult> FailSlow([FromQuery] string id)
    {
        var n = ExecutionCounter.Increment(id);
        await Task.Delay(800);
        return n == 1 ? StatusCode(503) : Ok(new { n });
    }

    [HttpPost("post")]
    [Idempotent]
    public async Task<IActionResult> Post([FromBody] PostRequest request)
    {
        var n = ExecutionCounter.Increment(request.Id);
        await Task.Delay(request.DelayMs);
        if (request.FailFirst && n == 1)
        {
            throw new InvalidOperationException("boom");
        }

        if (request.ConflictFirst && n == 1)
        {
            return Conflict();
        }

        Response.Headers.ETag = "\"v" + n + "\"";
        return Created("/api/v1/things/" + request.Id, new { n, Id = Guid.NewGuid() });
    }

    public sealed record PostRequest(string Id, int DelayMs = 300, bool FailFirst = false, string? Payload = null,
        bool ConflictFirst = false);
}
