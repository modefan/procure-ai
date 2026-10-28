using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using ProcureAi.BuildingBlocks.Tests.Web;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.OpenApi;

public sealed class HeaderParametersTransformerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> OperationAsync(string path, string method)
    {
        await using var app = await TestHost.StartAsync();
        var json = await app.GetTestClient().GetStringAsync("/openapi/v1.json", Ct);
        return JsonDocument.Parse(json).RootElement.GetProperty("paths").GetProperty(path).GetProperty(method).Clone();
    }

    private static JsonElement? Header(JsonElement operation, string name) =>
        operation.TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateArray()
                .Where(p => p.GetProperty("in").GetString() == "header" && p.GetProperty("name").GetString() == name)
                .Select(p => (JsonElement?)p).FirstOrDefault()
            : null;

    [Fact]
    public async Task Idempotent_operation_declares_required_Idempotency_Key()
    {
        var header = Header(await OperationAsync("/api/v1/conditional/create", "post"), "Idempotency-Key");

        header.ShouldNotBeNull();
        header.Value.GetProperty("required").GetBoolean().ShouldBeTrue();
        header.Value.GetProperty("schema").GetProperty("type").GetString().ShouldBe("string");
    }

    [Fact]
    public async Task IfMatch_operation_declares_required_If_Match()
    {
        var header = Header(await OperationAsync("/api/v1/conditional/update", "put"), "If-Match");

        header.ShouldNotBeNull();
        header.Value.GetProperty("required").GetBoolean().ShouldBeTrue();
        header.Value.GetProperty("schema").GetProperty("type").GetString().ShouldBe("string");
    }

    [Fact]
    public async Task Other_operations_declare_neither_header()
    {
        var plain = await OperationAsync("/api/v1/conditional/plain-post", "post");
        var read = await OperationAsync("/api/v1/conditional/plain", "get");

        Header(plain, "Idempotency-Key").ShouldBeNull();
        Header(plain, "If-Match").ShouldBeNull();
        Header(read, "If-Match").ShouldBeNull();
    }

    [Fact]
    public async Task Idempotent_operation_does_not_declare_If_Match()
    {
        Header(await OperationAsync("/api/v1/conditional/create", "post"), "If-Match").ShouldBeNull();
    }
}
