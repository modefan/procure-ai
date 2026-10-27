using ProcureAi.Audit.Application;
using ProcureAi.Audit.Infrastructure;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.BuildingBlocks.Web.Options;
using Wolverine;

namespace ProcureAi.Audit.Api;

/// <summary>The composition root of the Audit API: what is its own, on top of the shared <see cref="ServiceHost"/>.</summary>
public static class AuditApi
{
    public const string ServiceName = "audit-api";

    public static WebApplication Build(string[] args) =>
        ServiceHost.Build(args, ServiceName, builder =>
        {
            builder.Services.AddValidatedOptions<AuditOptions>(builder.Configuration, AuditOptions.SectionName);
            builder.Services.AddValidatedOptions<PagingOptions>(builder.Configuration, PagingOptions.SectionName);
            // Marten changes its schema (and the Wolverine message store in it) only in the --migrate run.
            builder.Services.AddAuditInfrastructure(builder.Configuration, builder.Configuration.GetValue<bool>(ServiceHost.MigratingSetting));
            builder.Host.UseWolverine(options => options.ConfigureAudit(builder.Configuration, ServiceName));
        });
}
