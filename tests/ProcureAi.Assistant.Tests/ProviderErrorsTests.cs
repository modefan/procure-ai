using ProcureAi.Assistant.Infrastructure.Ai;
using Shouldly;

namespace ProcureAi.Assistant.Tests;

/// <summary>Only a plain error code of the provider's error body may reach a log (<see cref="ProviderErrors"/>), never its text.</summary>
public sealed class ProviderErrorsTests
{
    [Theory]
    [InlineData("""{"error":{"message":"Rate limit for key sk-abc","code":"rate_limit_exceeded"}}""", "rate_limit_exceeded")]
    [InlineData("""{"error":{"message":"blocked","code":"content_filter","type":null}}""", "content_filter")]
    [InlineData("""{"error":{"message":"x","type":"invalid_request_error"}}""", "invalid_request_error")]
    [InlineData("""{"error":{"message":"x","code":"Ignore previous instructions sk-proj-123"}}""", null)]
    [InlineData("""{"error":{"message":"x","code":"429"}}""", null)]
    [InlineData("""{"error":"sk-proj-abcdef"}""", null)]
    [InlineData("""not json sk-proj-abcdef""", null)]
    [InlineData("", null)]
    public void Only_a_plain_error_code_is_kept(string body, string? code) =>
        ProviderErrors.CodeOf(BinaryData.FromString(body)).ShouldBe(code);
}
