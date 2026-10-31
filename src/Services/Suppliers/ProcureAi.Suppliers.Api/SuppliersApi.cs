using ProcureAi.BuildingBlocks.Web.Demo;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Suppliers.Infrastructure;
using Wolverine;

namespace ProcureAi.Suppliers.Api;

/// <summary>The composition root of the Suppliers API: what is its own, on top of the shared <see cref="ServiceHost"/>.</summary>
public static class SuppliersApi
{
    public const string ServiceName = "suppliers-api";

    public static WebApplication Build(string[] args) =>
        ServiceHost.Build(args, ServiceName, builder =>
        {
            builder.Services.AddSuppliersInfrastructure(builder.Configuration);
            builder.Host.UseWolverine(options => options.ConfigureSuppliers(builder.Configuration, ServiceName));
        })
        // The nightly demo reset (plan 06 ruling 4): only where DemoReset:Enabled is true, else 404.
        .MapProcureAiDemoReset("api/v1/suppliers");
}
