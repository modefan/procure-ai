using ProcureAi.BuildingBlocks.Web.Demo;
using ProcureAi.Budgets.Application;
using ProcureAi.Budgets.Infrastructure;
using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.BuildingBlocks.Web.Options;
using Wolverine;

namespace ProcureAi.Budgets.Api;

/// <summary>The composition root of the Budgets API: what is its own, on top of the shared <see cref="ServiceHost"/>.</summary>
public static class BudgetsApi
{
    public const string ServiceName = "budgets-api";

    public static WebApplication Build(string[] args) =>
        ServiceHost.Build(args, ServiceName, builder =>
        {
            builder.Services.AddValidatedOptions<BudgetsOptions>(builder.Configuration, BudgetsOptions.SectionName);
            builder.Services.AddBudgetsInfrastructure(builder.Configuration);
            builder.Services.AddScoped<BudgetScope>();
            builder.Host.UseWolverine(options => options.ConfigureBudgets(builder.Configuration, ServiceName));
        })
        // The nightly demo reset (plan 06 ruling 4): only where DemoReset:Enabled is true, else 404.
        .MapProcureAiDemoReset("api/v1/budgets");
}
