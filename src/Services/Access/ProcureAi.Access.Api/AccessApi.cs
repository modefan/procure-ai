using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.Access.Infrastructure;
using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.BuildingBlocks.Web.Auth;
using Wolverine;

namespace ProcureAi.Access.Api;

/// <summary>The composition root of the Access API: what is its own, on top of the shared <see cref="ServiceHost"/>.</summary>
public static class AccessApi
{
    public const string ServiceName = "access-api";

    public static WebApplication Build(string[] args) =>
        ServiceHost.Build(args, ServiceName, builder =>
        {
            builder.Services.AddAccessInfrastructure(builder.Configuration);
            // Access owns the matrix, so it answers permission checks from its own database instead of asking itself over HTTP.
            builder.Services.Replace(ServiceDescriptor.Scoped<IPermissionProvider, AccessPermissionProvider>());
            builder.Host.UseWolverine(options => options.ConfigureAccess(builder.Configuration));
        });
}
