using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Messaging.Caching;
using ProcureAi.BuildingBlocks.Web;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.BuildingBlocks.Web.Health;
using ProcureAi.BuildingBlocks.Web.Options;
using Wolverine;
using Wolverine.Runtime;

namespace ProcureAi.BuildingBlocks.Hosting;

/// <summary>
/// The composition root every service's <c>Program.cs</c> shares: web, caching, error mapping, the caller model and
/// readiness checks, plus the <c>--migrate</c> run mode. A service adds only what is its own (options, infrastructure,
/// Wolverine routes) in the <c>configure</c> callback.
/// </summary>
public static class ServiceHost
{
    /// <summary><c>&lt;Service&gt;.Api --migrate</c> migrates the database and Wolverine's message store, seeds demo data when enabled, and exits.</summary>
    public const string MigrateFlag = "--migrate";

    /// <summary>Set to <c>true</c> (<c>DemoSeed:Enabled</c>) by the AppHost's migrate resource.</summary>
    public const string DemoSeedSetting = "DemoSeed:Enabled";

    /// <summary>
    /// <c>true</c> while the process runs with <see cref="MigrateFlag"/>. A store that only changes its schema when allowed to
    /// (Marten, whose Wolverine message store follows Marten's <c>AutoCreateSchemaObjects</c>) reads it to allow that in the
    /// migrate run, and only there.
    /// </summary>
    public const string MigratingSetting = "Service:Migrating";

    private const string OpenApiToolAssembly = "GetDocument.Insider";

    /// <summary>Stand-ins for the secrets the Access client needs at startup, only while the OpenAPI document is written.</summary>
    private static readonly Dictionary<string, string?> OpenApiBuildPlaceholders = new()
    {
        ["Auth:Internal:TokenUrl"] = "http://localhost/openapi-build-only",
        ["Auth:Internal:ClientId"] = "openapi-build-only",
        ["Auth:Internal:ClientSecret"] = "openapi-build-only",
    };

    public static WebApplication Build(string[] args, string serviceName, Action<WebApplicationBuilder> configure)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (args.Contains(MigrateFlag))
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [MigratingSetting] = "true" });
        }

        builder.AddProcureAiWeb(serviceName);
        builder.AddProcureAiCaching();

        var services = builder.Services;
        services.AddValidatedOptions<ServiceOptions>(builder.Configuration, ServiceOptions.SectionName);
        // Expected outcomes (404, 403, 412, 422) become problem details inside the MVC pipeline.
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<ApplicationExceptionFilter>());
        // The service identity, the scoped caller context and the incoming-message holder.
        services.AddProcureAiCaller(serviceName);
        // Which requests and cost centers the caller may reach (admin key and services: all; others: what Access assigns).
        services.AddProcureAiCallerScope();
        // Handlers evict cached reads after their transaction commits (replaces the immediate-only invalidator of AddProcureAiCaching).
        services.Replace(ServiceDescriptor.Scoped<ICacheInvalidator, MessageCacheInvalidator>());

        // Every service asks Access for permissions (cached, fail-safe, evicted by AccessChanged); Access answers from its own database.
        if (serviceName != AccessClientSetup.AccessServiceName)
        {
            if (IsWritingOpenApiDocument())
            {
                // The build step that writes the OpenAPI document never calls Access, but options are validated when the host starts.
                builder.Configuration.AddInMemoryCollection(OpenApiBuildPlaceholders);
            }

            services.AddProcureAiAccessClient(builder.Configuration, serviceName);
        }

        configure(builder);

        // Ready only while the dependencies the API cannot serve without are reachable.
        var service = builder.Configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>();
        var databaseName = service?.DatabaseConnectionName
            ?? throw new InvalidOperationException($"'{ServiceOptions.SectionName}:{nameof(ServiceOptions.DatabaseConnectionName)}' is not configured.");
        services.AddPostgresReadiness(Required(builder.Configuration, databaseName));
        services.AddRabbitMqReadiness(Required(builder.Configuration, MessagingSetup.RabbitMqConnectionName));
        services.AddRedisReadiness();

        if (IsWritingOpenApiDocument())
        {
            // The build step that writes the OpenAPI document starts the host. It needs no database or broker.
            services.DisableAllExternalWolverineTransports();
            services.DisableAllWolverineMessagePersistence();
        }

        var app = builder.Build();
        app.UseProcureAiWeb();
        return app;
    }

    /// <summary>
    /// With <c>--migrate</c>: runs <paramref name="migrate"/> (the service's EF Core migrations), then Wolverine's message store
    /// (outbox and inbox tables), then <paramref name="seedDemoData"/> when <c>DemoSeed:Enabled</c> is true, and returns.
    /// Otherwise runs the application. Nothing migrates at normal startup.
    /// </summary>
    public static async Task RunAsync(
        WebApplication app,
        string[] args,
        Func<IServiceProvider, CancellationToken, Task> migrate,
        Func<IServiceProvider, CancellationToken, Task>? seedDemoData = null)
    {
        if (!args.Contains(MigrateFlag))
        {
            await app.RunAsync();
            return;
        }

        var cancellationToken = app.Lifetime.ApplicationStopping;
        await migrate(app.Services, cancellationToken);
        await app.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.MigrateAsync();
        if (seedDemoData is not null && app.Configuration.GetValue<bool>(DemoSeedSetting))
        {
            await seedDemoData(app.Services, cancellationToken);
        }
    }

    /// <summary>True while the <c>Microsoft.Extensions.ApiDescription.Server</c> build step runs the app to read its OpenAPI document.</summary>
    private static bool IsWritingOpenApiDocument() =>
        Assembly.GetEntryAssembly()?.GetName().Name == OpenApiToolAssembly;

    private static string Required(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name)
        ?? throw new InvalidOperationException($"Connection string '{name}' is not configured.");
}
