using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Api.Controllers;
using ProcureAi.Assistant.Application;
using Shouldly;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// The upload limit is applied before anything reads the body (the test server does not enforce Kestrel's body limit, so the filter is
/// checked directly): a declared size over the limit is 413 at once, otherwise the server's body limit is lowered to it.
/// </summary>
public sealed class UploadSizeLimitTests
{
    private const long Limit = 10L * 1024 * 1024 + UploadSizeLimitAttribute.FormOverheadBytes;

    [Fact]
    public void Declared_size_over_the_limit_is_413_before_the_body_is_read()
    {
        var (context, feature) = Context(contentLength: 31L * 1024 * 1024);

        Filter().OnResourceExecuting(context);

        var result = context.Result.ShouldBeOfType<ObjectResult>();
        result.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        feature.MaxRequestBodySize.ShouldBe(30_000_000L, "nothing else changed");
    }

    [Fact]
    public void Body_without_a_declared_size_gets_the_upload_limit()
    {
        var (context, feature) = Context(contentLength: null);

        Filter().OnResourceExecuting(context);

        context.Result.ShouldBeNull();
        feature.MaxRequestBodySize.ShouldBe(Limit);
    }

    [Fact]
    public void Limit_runs_before_the_idempotency_filter() =>
        new UploadSizeLimitAttribute().Order.ShouldBeLessThan(0);

    private static IResourceFilter Filter()
    {
        var services = new ServiceCollection().AddSingleton(Options.Create(new AssistantOptions())).BuildServiceProvider();
        return (IResourceFilter)new UploadSizeLimitAttribute().CreateInstance(services);
    }

    private static (ResourceExecutingContext Context, FakeBodySize Feature) Context(long? contentLength)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentLength = contentLength;
        var feature = new FakeBodySize();
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return (new ResourceExecutingContext(action, [], []), feature);
    }

    private sealed class FakeBodySize : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; } = 30_000_000L;
    }
}
