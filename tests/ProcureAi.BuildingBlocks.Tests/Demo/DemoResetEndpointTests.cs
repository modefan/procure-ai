using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Tests.Web;
using ProcureAi.BuildingBlocks.Web.Demo;
using Shouldly;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Tests.Demo;

/// <summary>
/// <c>POST …/internal/demo/reset</c> deletes every tenant's data, so only the admin API key may call it (plan 07 Task 0a, plan 06 carry-over 8):
/// a service account passes the internal policy of the other internal endpoints, but not this one. ProcureAi.DemoData calls it with the key.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class DemoResetEndpointTests
{
    private const string InternalClient = "procure-internal";
    private const string Reset = "/api/v1/test/internal/demo/reset";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CountingResetter : IDemoResetter
    {
        public int Calls { get; private set; }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication App, CountingResetter Resetter)> StartAsync()
    {
        var resetter = new CountingResetter();
        var app = await TestHost.StartAsync(
            new() { ["DemoReset:Enabled"] = "true", ["Auth:InternalClientId"] = InternalClient },
            configure: builder =>
            {
                builder.Services.AddFusionCache();
                builder.Services.AddSingleton<IDemoResetter>(resetter);
            },
            map: app => app.MapProcureAiDemoReset("api/v1/test"));
        return (app, resetter);
    }

    private static HttpRequestMessage Post(string? apiKey = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Reset);
        if (apiKey is not null)
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }

    [Fact]
    public async Task Demo_reset_with_service_token_is_403()
    {
        var (app, resetter) = await StartAsync();
        await using var _ = app;

        using var service = await app.GetTestClient().SendAsync(Post(bearer: TestAuth.CreateServiceToken(InternalClient)), Ct);
        using var user = await app.GetTestClient().SendAsync(Post(bearer: TestAuth.CreateToken("user-1", "acme", "admin")), Ct);
        using var anonymous = await app.GetTestClient().SendAsync(Post(), Ct);

        service.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a service account may call other internal endpoints, not the reset");
        user.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        resetter.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Demo_reset_with_admin_key_is_204()
    {
        var (app, resetter) = await StartAsync();
        await using var _ = app;

        using var response = await app.GetTestClient().SendAsync(Post(apiKey: TestAuth.ApiKey), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        resetter.Calls.ShouldBe(1);
    }
}
