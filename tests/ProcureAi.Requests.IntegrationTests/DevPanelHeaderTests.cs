using System.Net;
using Shouldly;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>The dev panel headers on the real Requests API: policy, tenant, instance, cache and the always-on trace ID.</summary>
[Collection(InfraCollection.Name)]
public sealed class DevPanelHeaderTests(InfraFixture infra) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(infra, CapturingSink.Shared, new Dictionary<string, string?> { ["DevPanel:Enabled"] = "true" });
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static HttpRequestMessage List(string tenant, bool devPanel)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/requests");
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", tenant);
        if (devPanel)
        {
            request.Headers.Add("X-Dev-Panel", "1");
        }

        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task Dev_panel_headers_name_policy_tenant_instance_and_cache()
    {
        var tenant = $"dev-{Guid.NewGuid():N}"[..20];

        using var first = await _client.SendAsync(List(tenant, devPanel: true), Ct);
        using var second = await _client.SendAsync(List(tenant, devPanel: true), Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        Header(first, "X-Policy").ShouldBe("requests.read");
        Header(first, "X-Tenant").ShouldBe(tenant);
        Header(first, "X-Instance").ShouldStartWith("requests-api/");
        Header(first, "X-Cache").ShouldBe("miss");
        Header(first, "X-Trace-Id")!.Length.ShouldBe(32);
        Header(second, "X-Cache").ShouldStartWith("hit; age-ms=");
    }

    [Fact]
    public async Task Only_the_trace_id_without_the_request_header()
    {
        using var response = await _client.SendAsync(List($"dev-{Guid.NewGuid():N}"[..20], devPanel: false), Ct);

        Header(response, "X-Trace-Id").ShouldNotBeNull();
        Header(response, "X-Policy").ShouldBeNull();
        Header(response, "X-Cache").ShouldBeNull();
        Header(response, "X-Instance").ShouldBeNull();
    }
}
