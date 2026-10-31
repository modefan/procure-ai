using ProcureAi.BuildingBlocks.Web.Demo;
using ProcureAi.Approvals.Application;
using ProcureAi.Approvals.Infrastructure;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.BuildingBlocks.Web.Options;
using Wolverine;

namespace ProcureAi.Approvals.Api;

/// <summary>The composition root of the Approvals API: what is its own, on top of the shared <see cref="ServiceHost"/>.</summary>
public static class ApprovalsApi
{
    public const string ServiceName = "approvals-api";

    public static WebApplication Build(string[] args) =>
        ServiceHost.Build(args, ServiceName, builder =>
        {
            builder.Services.AddValidatedOptions<ApprovalsOptions>(builder.Configuration, ApprovalsOptions.SectionName);
            builder.Services.AddApprovalsInfrastructure(builder.Configuration);
            builder.Host.UseWolverine(options => options.ConfigureApprovals(builder.Configuration, ServiceName));
        })
        // The nightly demo reset (plan 06 ruling 4): only where DemoReset:Enabled is true, else 404.
        .MapProcureAiDemoReset("api/v1/approvals");
}
