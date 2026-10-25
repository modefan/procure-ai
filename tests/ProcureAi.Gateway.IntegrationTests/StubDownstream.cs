using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>What the stub downstream service saw.</summary>
public sealed record SeenRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>A real Kestrel server standing in for requests-api and the web app; records every request it gets.</summary>
public sealed class StubDownstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<SeenRequest> _seen = new();

    private StubDownstream(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public IReadOnlyList<SeenRequest> Seen => [.. _seen];

    public SeenRequest Last => _seen.Last();

    public static async Task<StubDownstream> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        StubDownstream? stub = null;
        app.Run(async context =>
        {
            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            stub!._seen.Enqueue(new SeenRequest(context.Request.Method, context.Request.Path.Value!, headers));
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { stub = true, host = context.Request.Host.Value, path = context.Request.Path.Value });
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        stub = new StubDownstream(app, url);
        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
