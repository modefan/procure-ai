using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Assistant;
using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Invoicing;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Contracts.Requests;
using Wolverine;
using Wolverine.RabbitMQ;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// The real Notifications API (its Program) on a test server, against the shared RabbitMQ, Redis and Mailpit, with the stub Access
/// service. Two instances on one Redis stand in for two pods (the SignalR backplane). No database. <paramref name="configure"/> adds
/// what one test needs (a fake presence clock, other presence timings).
/// </summary>
public sealed class NotificationsApiFactory(NotificationsApp app, Action<IWebHostBuilder>? configure = null)
    : ServiceApiFactory<Program>(null, null, app.Rabbit.ConnectionString, app.Redis.ConnectionString)
{
    public const string AppBaseUrl = "https://app.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in app.Access.Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting("Auth:InternalClientId", NotificationsApp.InternalClientId);
        // High enough that long polling by several clients never meets the limiter.
        builder.UseSetting("RateLimiting:PermitLimit", "10000");
        builder.UseSetting("Notifications:Smtp:Host", app.Mailpit.SmtpHost);
        builder.UseSetting("Notifications:Smtp:Port", app.Mailpit.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Notifications:Smtp:From", "no-reply@procure-ai.test");
        builder.UseSetting("Notifications:Smtp:AppBaseUrl", AppBaseUrl);
        configure?.Invoke(builder);
    }
}

/// <summary>RabbitMQ, Redis, the stub Access, two Notifications instances and a publisher playing the other services.</summary>
public sealed class NotificationsApp : IAsyncLifetime
{
    public const string InternalClientId = "procure-internal";

    public RabbitFixture Rabbit { get; } = new();

    public RedisFixture Redis { get; } = new();

    public MailpitFixture Mailpit { get; } = new();

    public RabbitProbe Probe => new(Rabbit.ConnectionString);

    public StubAccessServer Access { get; private set; } = null!;

    /// <summary>Instance A.</summary>
    public NotificationsApiFactory A { get; private set; } = null!;

    /// <summary>Instance B, on the same Redis (backplane) and the same queue (competing consumers).</summary>
    public NotificationsApiFactory B { get; private set; } = null!;

    private IHost? _publisher;

    public async ValueTask InitializeAsync()
    {
        await Rabbit.InitializeAsync();
        await Redis.InitializeAsync();
        await Mailpit.InitializeAsync();
        Access = await StubAccessServer.StartAsync();
        A = new NotificationsApiFactory(this);
        B = new NotificationsApiFactory(this);
        // Start both now: each binds the live queue to the exchanges before anything is published.
        _ = A.Server;
        _ = B.Server;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:rabbitmq"] = Rabbit.ConnectionString })
            .Build();
        _publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(options =>
            {
                options.Discovery.DisableConventionalDiscovery();
                options.UseProcureAiTransport(configuration);
                options.PublishMessage<RequestSubmitted>().ToRabbitExchange(Exchanges.Requests);
                options.PublishMessage<ApprovalRequested>().ToRabbitExchange(Exchanges.Approvals);
                options.PublishMessage<RequestApproved>().ToRabbitExchange(Exchanges.Approvals);
                options.PublishMessage<RequestRejected>().ToRabbitExchange(Exchanges.Approvals);
                options.PublishMessage<BudgetReserved>().ToRabbitExchange(Exchanges.Budgets);
                options.PublishMessage<PurchaseOrderCreated>().ToRabbitExchange(Exchanges.Purchasing);
                options.PublishMessage<InvoiceMatched>().ToRabbitExchange(Exchanges.Invoicing);
                options.PublishMessage<PolicyDocumentProgressed>().ToRabbitExchange(Exchanges.Assistant);
                options.PublishMessage<PolicyDocumentStatusChanged>().ToRabbitExchange(Exchanges.Assistant);
                options.PublishMessage<ProcureAi.Contracts.Access.AccessChanged>().ToRabbitExchange(Exchanges.Access);
            })
            .StartAsync();
    }

    /// <summary>Publishes an event the way its service does (to its exchange, through Wolverine).</summary>
    public async Task PublishAsync<T>(T integrationEvent)
        where T : IIntegrationEvent =>
        await _publisher!.MessageBus().PublishAsync(integrationEvent);

    public async ValueTask DisposeAsync()
    {
        if (_publisher is not null)
        {
            await _publisher.StopAsync();
            _publisher.Dispose();
        }

        if (A is not null)
        {
            await A.DisposeAsync();
        }

        if (B is not null)
        {
            await B.DisposeAsync();
        }

        if (Access is not null)
        {
            await Access.DisposeAsync();
        }

        await Mailpit.DisposeAsync();
        await Redis.DisposeAsync();
        await Rabbit.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class NotificationsCollection : ICollectionFixture<NotificationsApp>
{
    public const string Name = "notifications-infra";
}
