using Microsoft.Extensions.Hosting;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>Query values never leave the process in logs or traces; the parameter names do (plan 07 Task 0a).</summary>
public sealed class QueryStringRedactionTests
{
    [Theory]
    [InlineData("?ticket=abc123", "?ticket=***")]
    [InlineData("ticket=abc123", "ticket=***")]
    [InlineData("?ticket=abc&page=2", "?ticket=***&page=***")]
    [InlineData("?ticket=", "?ticket=***")]
    [InlineData("?abc123", "?***")]
    [InlineData("?a=1&&b=x=y", "?a=***&&b=***")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Every_value_is_masked_and_names_stay(string? query, string? expected) =>
        QueryStringRedaction.Redact(query).ShouldBe(expected);
}
