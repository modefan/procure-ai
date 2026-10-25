using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

public class VersionETagTests
{
    [Fact]
    public void Format_quotes_the_version() => VersionETag.Format(42).ShouldBe("\"42\"");

    [Theory]
    [InlineData("\"7\"", 7u)]
    [InlineData("W/\"7\"", 7u)]
    [InlineData("7", 7u)]
    [InlineData("  \"7\"  ", 7u)]
    public void Parses_strong_weak_and_bare_tags(string header, uint expected)
    {
        VersionETag.TryParse(header, out var version).ShouldBeTrue();
        version.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("\"abc\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"1\", \"2\"")]
    [InlineData("\"99999999999\"")]
    public void Rejects_what_is_not_a_single_version(string? header) =>
        VersionETag.TryParse(header, out _).ShouldBeFalse();
}

public class CacheTagsTests
{
    private sealed class FakeUser(string? tenant) : ICurrentUser
    {
        public string UserId => "u";
        public string? TenantId => tenant;
        public IReadOnlySet<string> Roles => new HashSet<string>();
        public bool IsAdminApiKey => false;
        public string? CallingService => null;
        public string? OnBehalfOfUser => null;
    }

    private static HttpContext ContextWith(params (string Name, object Value)[] routeValues)
    {
        var http = new DefaultHttpContext();
        foreach (var (name, value) in routeValues)
        {
            http.Request.RouteValues[name] = value;
        }

        return http;
    }

    [Fact]
    public void Fills_in_tenant_and_route_values()
    {
        var tags = CacheTags.Resolve(["requests:{tenant}:{id}", "requests:{tenant}:list"], ContextWith(("id", 5)), new FakeUser("acme"));

        tags.ShouldBe(["requests:acme:5", "requests:acme:list"]);
    }

    [Fact]
    public void Skips_a_template_whose_route_value_is_missing_and_marks_no_tenant()
    {
        var tags = CacheTags.Resolve(["x:{tenant}:{id}", "x:{tenant}"], ContextWith(), new FakeUser(null));

        tags.ShouldBe(["x:~"]);
    }
}
