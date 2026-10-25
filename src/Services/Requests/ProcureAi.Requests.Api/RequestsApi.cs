using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.BuildingBlocks.Web;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Health;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Requests.Application;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Infrastructure;
using Wolverine;
using Wolverine.Runtime;

namespace ProcureAi.Requests.Api;

/// <summary>The composition root of the Requests API.</summary>
public static class RequestsApi
{
    public const string ServiceName = "requests-api";

    /// <summary><c>ProcureAi.Requests.Api --migrate</c> migrates the databases and exits.</summary>
    public const string MigrateFlag = "--migrate";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddProcureAiWeb(ServiceName);
        builder.AddProcureAiCaching();

        var services = builder.Services;
        services.AddValidatedOptions<PagingOptions>(builder.Configuration, PagingOptions.SectionName);
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<RequestsExceptionFilter>());
        // A scoped class, not a lambda: Wolverine's generated handler code constructs it from its constructor.
        services.AddScoped<ICallerContext, ApiCallerContext>();
        services.AddScoped<IncomingMessage>();
        services.AddRequestsInfrastructure(builder.Configuration);
        // Ready only while the dependencies the API cannot serve without are reachable.
        services.AddPostgresReadiness(builder.Configuration.GetConnectionString(DependencyInjection.DatabaseConnectionName)!);
        services.AddRabbitMqReadiness(builder.Configuration.GetConnectionString(DependencyInjection.RabbitMqConnectionName)!);
        services.AddRedisReadiness();

        builder.Host.UseWolverine(options =>
        {
            options.ConfigureRequests(builder.Configuration);
            options.AddCallerPropagation();
        });

        if (IsWritingOpenApiDocument())
        {
            // The build step that writes the OpenAPI document starts the host. It needs no database or broker.
            builder.Services.DisableAllExternalWolverineTransports();
            builder.Services.DisableAllWolverineMessagePersistence();
        }

        var app = builder.Build();
        app.UseProcureAiWeb();
        return app;
    }

    /// <summary>True while the <c>Microsoft.Extensions.ApiDescription.Server</c> build step runs the app to read its OpenAPI document.</summary>
    private static bool IsWritingOpenApiDocument() =>
        Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

    /// <summary>The database schema (EF Core migrations) and Wolverine's message store (outbox/inbox tables).</summary>
    public static async Task MigrateAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        await app.Services.MigrateRequestsDatabaseAsync(cancellationToken);
        await app.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.MigrateAsync();
    }
}
