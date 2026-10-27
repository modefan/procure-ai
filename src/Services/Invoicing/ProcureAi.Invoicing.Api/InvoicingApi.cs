using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.BuildingBlocks.Web.Webhooks;
using ProcureAi.Invoicing.Application;
using ProcureAi.Invoicing.Infrastructure;
using Wolverine;

namespace ProcureAi.Invoicing.Api;

/// <summary>The composition root of the Invoicing API: what is its own, on top of the shared <see cref="ServiceHost"/>.</summary>
public static class InvoicingApi
{
    public const string ServiceName = "invoicing-api";

    public static WebApplication Build(string[] args) =>
        ServiceHost.Build(args, ServiceName, builder =>
        {
            builder.Services.AddValidatedOptions<PagingOptions>(builder.Configuration, PagingOptions.SectionName);
            builder.Services.AddValidatedOptions<InvoicingOptions>(builder.Configuration, InvoicingOptions.SectionName);
            builder.Services.AddInvoicingInfrastructure(builder.Configuration);
            builder.Services.AddErpGateway(builder.Configuration);
            // The supplier's signed invoice webhooks (Webhooks:Sources:supplier:Secret).
            builder.Services.AddProcureAiWebhooks(builder.Configuration);
            builder.Host.UseWolverine(options => options.ConfigureInvoicing(builder.Configuration, ServiceName));
        });
}
