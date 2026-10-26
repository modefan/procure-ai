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
        });
}
