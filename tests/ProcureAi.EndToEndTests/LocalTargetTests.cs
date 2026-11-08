using Shouldly;

namespace ProcureAi.EndToEndTests;

/// <summary>
/// Which gateways count as this PC (Task 12 re-review minor 2): only for those may the fixture fall back to the local admin key without a
/// target user-secrets store. No fixture: these run without a browser or a deployment.
/// </summary>
public sealed class LocalTargetTests
{
    [Theory]
    [InlineData("https://localhost:7100/")]
    [InlineData("https://127.0.0.1:8443/")]
    [InlineData("http://[::1]:5000/")]
    [InlineData("https://procure.localtest.me/")]
    [InlineData("https://gateway.localhost/")]
    public void A_gateway_on_this_pc_is_local(string url) => AppFixture.IsLocalTarget(new Uri(url)).ShouldBeTrue();

    [Theory]
    [InlineData("https://procure.daleabels.com/")]
    [InlineData("https://localtest.me.example.com/")]
    [InlineData("https://notlocalhost/")]
    [InlineData("https://10.0.0.5/")]
    [InlineData("https://203.0.113.7/")]
    public void Any_other_gateway_is_remote(string url) => AppFixture.IsLocalTarget(new Uri(url)).ShouldBeFalse();
}
