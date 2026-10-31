using ProcureAi.BuildingBlocks.Web.Demo;
using ProcureAi.Assistant.Infrastructure;
using ProcureAi.BuildingBlocks.Hosting;
using Wolverine;

namespace ProcureAi.Assistant.Api;

/// <summary>
/// The composition root of the Assistant API: what is its own, on top of the shared <see cref="ServiceHost"/>. Postgres (pgvector, the
/// outbox and inbox) is the main store, MongoDB the second one (<c>Service:SecondaryMongoConnectionName</c>); the model providers and
/// pipelines come from <c>AI</c> (Fake by default: no key, no account).
/// </summary>
public static class AssistantApi
{
    public const string ServiceName = "assistant-api";

    public static WebApplication Build(string[] args) =>
        ServiceHost.Build(args, ServiceName, builder =>
        {
            builder.Services.AddAssistantInfrastructure(builder.Configuration);
            builder.Host.UseWolverine(options => options.ConfigureAssistant(builder.Configuration, ServiceName));
        })
        // The nightly demo reset (plan 06 ruling 4): only where DemoReset:Enabled is true, else 404.
        .MapProcureAiDemoReset("api/v1/assistant");
}
