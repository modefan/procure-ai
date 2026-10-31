using ProcureAi.BuildingBlocks.Web.Demo;
using Microsoft.AspNetCore.Http.Connections;
using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Notifications.Api.Hubs;
using ProcureAi.Notifications.Application.Email;
using ProcureAi.Notifications.Application.Inbox;
using ProcureAi.Notifications.Application.Live;
using ProcureAi.Notifications.Application.Presence;
using ProcureAi.Notifications.Infrastructure;
using Wolverine;

namespace ProcureAi.Notifications.Api;

/// <summary>
/// The composition root of the Notifications API: the live hub (SignalR, Redis backplane) fed by a buffered RabbitMQ listener on the
/// services' exchanges, presence, the bell (<c>api/v1/notifications</c>) and email. No database (<c>Service:Store=None</c>, plan 04
/// ruling 1): presence, bell items and email dedup keys are in Redis.
/// </summary>
public static class NotificationsApi
{
    public const string ServiceName = "notifications-api";

    public static WebApplication Build(string[] args)
    {
        var app = ServiceHost.Build(args, ServiceName, builder =>
        {
            var services = builder.Services;
            services.AddValidatedOptions<NotificationsOptions>(builder.Configuration, NotificationsOptions.SectionName);
            services.AddValidatedOptions<PresenceOptions>(builder.Configuration, PresenceOptions.SectionName);
            services.AddValidatedOptions<BellOptions>(builder.Configuration, BellOptions.SectionName);
            services.AddValidatedOptions<SmtpOptions>(builder.Configuration, SmtpOptions.SectionName);
            services.AddNotificationsInfrastructure();
            services.AddAuthorizationBuilder().AddLiveHubPolicy();
            services.AddSignalR().AddNotificationsBackplane(builder.Configuration);
            services.AddSingleton<ILivePushes, SignalRLivePushes>();
            builder.Host.UseWolverine(options => options.ConfigureNotifications(builder.Configuration, ServiceName));
        });

        // WebSockets through the gateway; long polling for clients that cannot (the in-memory test server).
        app.MapHub<LiveHub>(LiveHubContract.Path, hub => hub.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling);
        // The nightly demo reset (plan 06 ruling 4): only where DemoReset:Enabled is true, else 404.
        app.MapProcureAiDemoReset("api/v1/notifications");
        return app;
    }
}
