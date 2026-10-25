using ProcureAi.BuildingBlocks.Messaging.Hosting;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Requests.Application;
using ProcureAi.Requests.Infrastructure;
using Wolverine;

namespace ProcureAi.Requests.Api;

/// <summary>The composition root of the Requests API: what is its own, on top of the shared <see cref="ServiceHost"/>.</summary>
public static class RequestsApi
{
    public const string ServiceName = "requests-api";

    public static WebApplication Build(string[] args) =>
        ServiceHost.Build(args, ServiceName, builder =>
        {
            builder.Services.AddValidatedOptions<PagingOptions>(builder.Configuration, PagingOptions.SectionName);
            builder.Services.AddRequestsInfrastructure(builder.Configuration);
            builder.Host.UseWolverine(options => options.ConfigureRequests(builder.Configuration));
        });
}
