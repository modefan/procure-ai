using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.BuildingBlocks.Tests.Web;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/test")]
public sealed class TestController(ILogger<TestController> logger) : ControllerBase
{
    [HttpGet("ping")]
    public IActionResult Ping() => Ok("pong");

    [HttpGet("domain-error")]
    public IActionResult DomainError() => throw new DomainException("Budget exceeded");

    [HttpGet("log")]
    public IActionResult Log()
    {
        logger.LogInformation("information-event");
        logger.LogWarning("warning-event");
        return Ok();
    }

    [HttpPost("validate")]
    public IActionResult Validate([FromBody] ValidateRequest request) => Ok(request);

    public sealed class ValidateRequest
    {
        [Required]
        public string? Name { get; set; }
    }
}
