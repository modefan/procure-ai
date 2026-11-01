using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Diagnostics;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Diagnostics;

/// <summary>
/// Final review F4: the dev panel writes its headers in <c>OnStarting</c>. A throw there used to fail the response itself; now it is
/// logged and the response goes out (with the trace ID, without the dev panel headers that failed).
/// </summary>
public sealed class DevPanelMiddlewareTests
{
    [Fact]
    public async Task A_throw_while_writing_the_dev_panel_headers_is_logged_and_the_response_still_starts()
    {
        var logger = new ListLogger();
        var feature = new RecordingResponseFeature();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<ICurrentUser, ThrowingUser>().BuildServiceProvider(),
        };
        context.Features.Set<IHttpResponseFeature>(feature);
        context.Request.Headers[DevPanelHeaders.Request] = "1";
        var middleware = new DevPanelMiddleware(_ => Task.CompletedTask, Options.Create(new DevPanelOptions { Enabled = true }), logger);

        await middleware.InvokeAsync(context);
        await Should.NotThrowAsync(feature.StartAsync);

        context.Response.Headers.ContainsKey(DevPanelHeaders.TraceId).ShouldBeTrue("the trace ID is written before the dev panel headers");
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    private sealed class RecordingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = [];

        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));

        public async Task StartAsync()
        {
            foreach (var (callback, state) in _starting)
            {
                await callback(state);
            }
        }
    }

    private sealed class ThrowingUser : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public string UserId => "u-1";

        public string? TenantId => throw new InvalidOperationException("tenant claim broken");

        public IReadOnlySet<string> Roles => new HashSet<string>();

        public bool IsAdminApiKey => false;

        public bool IsServiceAccount => false;

        public string? CallingService => null;

        public string? OnBehalfOfUser => null;
    }

    private sealed class ListLogger : ILogger<DevPanelMiddleware>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }
}
