using ProcureAi.Gateway.Bff;

namespace ProcureAi.Gateway.IntegrationTests;

public sealed class ReturnUrlTests
{
    [Theory]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("%2F%2Fevil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("http://localhost/x", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/ok\r\nSet-Cookie: x=1", "/")]
    [InlineData("/a\tb", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    [InlineData("/", "/")]
    [InlineData("/orders/7?tab=lines", "/orders/7?tab=lines")]
    public void Only_a_local_path_is_accepted(string? input, string expected) =>
        BffEndpoints.SafeReturnUrl(input).ShouldBe(expected);
}
