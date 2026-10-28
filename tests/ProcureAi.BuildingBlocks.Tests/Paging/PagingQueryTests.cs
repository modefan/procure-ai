using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using ProcureAi.BuildingBlocks.Tests.Web;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Paging;

public sealed class PagingQueryTests
{
    private const string Path = "/api/v1/conditional/paged";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string query, Dictionary<string, string?>? config = null)
    {
        await using var app = await TestHost.StartAsync(config);
        using var request = new HttpRequestMessage(HttpMethod.Get, Path + query);
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", "acme");
        using var response = await app.GetTestClient().SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?page=-3", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?pageSize=100000", "pageSize")]
    public async Task Invalid_paging_is_a_400_with_the_field_name(string query, string field)
    {
        var (status, body) = await GetAsync(query);

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Defaults_are_page_1_and_the_configured_default_size()
    {
        var (status, body) = await GetAsync("");

        status.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("page").GetInt32().ShouldBe(1);
        body.GetProperty("pageSize").GetInt32().ShouldBe(25);
    }

    [Fact]
    public async Task Limits_come_from_configuration()
    {
        var config = new Dictionary<string, string?> { ["Paging:MaxPageSize"] = "10", ["Paging:DefaultPageSize"] = "5" };

        (await GetAsync("", config)).Body.GetProperty("pageSize").GetInt32().ShouldBe(5);
        (await GetAsync("?pageSize=10", config)).Status.ShouldBe(HttpStatusCode.OK);
        (await GetAsync("?pageSize=11", config)).Status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Valid_values_pass_through()
    {
        var (status, body) = await GetAsync("?page=3&pageSize=100");

        status.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("page").GetInt32().ShouldBe(3);
        body.GetProperty("pageSize").GetInt32().ShouldBe(100);
    }
}
