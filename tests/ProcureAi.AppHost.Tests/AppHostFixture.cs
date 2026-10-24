using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.AppHost.Tests;

/// <summary>Starts the AppHost once for all infrastructure tests.</summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    private DistributedApplication? _app;

    public DistributedApplication App => _app ?? throw new InvalidOperationException("AppHost not started.");

    /// <summary>Resource caps from tests/testcontainers.settings.json (same values as the AppHost Resources section).</summary>
    public IConfigurationSection Limits { get; } = new ConfigurationBuilder()
        .AddJsonFile("testcontainers.settings.json", optional: false)
        .Build()
        .GetSection("Resources");

    public async ValueTask InitializeAsync()
    {
        // Tests never keep data: no volumes, so every run starts from an empty database.
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ProcureAi_AppHost>(
            ["Persistence:UseDataVolumes=false"]);
        _app = await builder.BuildAsync();
        await _app.StartAsync();
    }

    public async Task WaitHealthyAsync(string resource)
    {
        using var cts = new CancellationTokenSource(StartupTimeout);
        await App.ResourceNotifications.WaitForResourceHealthyAsync(resource, cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    public async Task<string> ContainerIdAsync(string resource)
    {
        using var cts = new CancellationTokenSource(StartupTimeout);
        var evt = await App.ResourceNotifications.WaitForResourceAsync(
            resource, e => e.Snapshot.Properties.Any(p => p.Name == "container.id" && p.Value is string), cts.Token);
        return (string)evt.Snapshot.Properties.First(p => p.Name == "container.id").Value!;
    }
}

[CollectionDefinition(Name)]
public sealed class InfrastructureCollection : ICollectionFixture<AppHostFixture>
{
    public const string Name = "infrastructure";
}
