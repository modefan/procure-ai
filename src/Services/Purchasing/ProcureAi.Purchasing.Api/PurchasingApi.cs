using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.BuildingBlocks.Web.Webhooks;
using ProcureAi.Purchasing.Application;
using ProcureAi.Purchasing.Infrastructure;
using Wolverine;

namespace ProcureAi.Purchasing.Api;

/// <summary>The composition root of the Purchasing API: what is its own, on top of the shared <see cref="ServiceHost"/>.</summary>
public static class PurchasingApi
{
    public const string ServiceName = "purchasing-api";

    public static WebApplication Build(string[] args) =>
        ServiceHost.Build(args, ServiceName, builder =>
        {
            builder.Services.AddValidatedOptions<PurchasingOptions>(builder.Configuration, PurchasingOptions.SectionName);
            builder.Services.AddPurchasingInfrastructure(builder.Configuration);
            builder.Services.AddSupplierGateway(builder.Configuration);
            // The supplier's signed webhooks (Webhooks:Sources:supplier:Secret).
            builder.Services.AddProcureAiWebhooks(builder.Configuration);
            builder.Host.UseWolverine(options => options.ConfigurePurchasing(builder.Configuration, ServiceName));
        });
}
