using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Assistant.Application.Tools;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.BuildingBlocks.Web.Options;

namespace ProcureAi.Assistant.Infrastructure.Tools;

public static class ToolsSetup
{
    /// <summary>
    /// The model's tools (plan 05 Task 5b): <see cref="AssistantToolsFactory"/> and the four lookups, each a client of another service that
    /// relays the caller's credential (<c>AddProcureAiUserClient</c>, ruling 3) with its retries and timeouts from
    /// <c>Assistant:ToolClients</c>. The AppHost gives the Assistant the addresses of these services (<c>CallsServices</c>).
    /// </summary>
    public static IServiceCollection AddAssistantTools(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<ToolClientsOptions>(configuration, ToolClientsOptions.SectionName);
        services.AddProcureAiUserClient<IRequestsLookup, RequestsLookup>(RequestsLookup.ServiceName).WithResilienceFrom<ToolClientsOptions>();
        services.AddProcureAiUserClient<IApprovalsLookup, ApprovalsLookup>(ApprovalsLookup.ServiceName).WithResilienceFrom<ToolClientsOptions>();
        services.AddProcureAiUserClient<IBudgetsLookup, BudgetsLookup>(BudgetsLookup.ServiceName).WithResilienceFrom<ToolClientsOptions>();
        services.AddProcureAiUserClient<ISuppliersLookup, SuppliersLookup>(SuppliersLookup.ServiceName).WithResilienceFrom<ToolClientsOptions>();
        services.AddScoped<AssistantToolsFactory>();
        return services;
    }
}
