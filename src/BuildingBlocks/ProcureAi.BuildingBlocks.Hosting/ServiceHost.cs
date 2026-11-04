using System.Reflection;
using JasperFx;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Messaging;
using MongoDB.Driver;
using ProcureAi.BuildingBlocks.Messaging.Caching;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using ProcureAi.BuildingBlocks.Web;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.BuildingBlocks.Web.Health;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.BuildingBlocks.Web.Status;
using ProcureAi.BuildingBlocks.Messaging.Status;
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

    /// <summary><c>&lt;Service&gt;.Api codegen write</c> writes the service's Wolverine (and Marten) code to <c>Internal/Generated</c> and exits.</summary>
    public const string CodegenCommand = "codegen";

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
        // Wolverine:TypeLoadMode (Dynamic | Auto | Static): the images run Static on the code committed under Internal/Generated.
        services.AddProcureAiCodeGeneration(builder.Configuration);
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

        // The status snapshot every instance writes to Redis (health, breakers, message counts, the service's own contributors). Not while
        // the OpenAPI document is written: that run has no Redis.
        services.AddSingleton<IWolverineCounts, WolverineCounts>();
        services.AddProcureAiStatus(builder.Configuration, publish: !IsWritingOpenApiDocument());

        // Every Postgres consumer of the service (EF Core, Wolverine's store, readiness) shares one pool of Database:MaxPoolSize connections.
        services.AddValidatedOptions<DatabaseOptions>(builder.Configuration, DatabaseOptions.SectionName);
        var storeOptions = builder.Configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>() ?? new ServiceOptions();
        if (storeOptions.Store == ServiceStore.Postgres && !string.IsNullOrWhiteSpace(storeOptions.DatabaseConnectionName))
        {
            PostgresPooling.ApplyMaxPoolSize(builder.Configuration, builder.Configuration, storeOptions.DatabaseConnectionName);
        }

        configure(builder);

        // Ready only while the dependencies the API cannot serve without are reachable.
        var service = builder.Configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>() ?? new ServiceOptions();
        if (service.Store == ServiceStore.MongoDB)
        {
            // The service registers its client with AddProcureAiMongo; the check pings through it.
            Required(builder.Configuration, DatabaseName(service));
            MongoReadinessCheck.AddMongoReadiness(services);
        }
        else if (service.Store == ServiceStore.Postgres)
        {
            services.AddPostgresReadiness(Required(builder.Configuration, DatabaseName(service)));
            if (!string.IsNullOrWhiteSpace(service.SecondaryMongoConnectionName))
            {
                // A second database on MongoDB (the Assistant's chat history): ready only while both answer.
                Required(builder.Configuration, service.SecondaryMongoConnectionName);
                MongoReadinessCheck.AddMongoReadiness(services);
            }
        }

        // ServiceStore.None (Notifications): no database to be ready for.

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
    /// A MongoDB service (<c>Service:Store=MongoDB</c>) has no message store: it runs every registered <see cref="IMongoMigration"/>
    /// (indexes), then <paramref name="migrate"/> when given, then the seed. A Postgres service with a second database on MongoDB
    /// (<see cref="ServiceOptions.SecondaryMongoConnectionName"/>) runs its <see cref="IMongoMigration"/>s first, then the Postgres steps.
    /// A service without a database (<c>Service:Store=None</c>) has nothing to migrate: only <paramref name="migrate"/> and the seed run,
    /// when given (the AppHost adds no migrate resource for it).
    /// Otherwise runs the application. Nothing migrates at normal startup.
    /// </summary>
    public static async Task RunAsync(
        WebApplication app,
        string[] args,
        Func<IServiceProvider, CancellationToken, Task>? migrate,
        Func<IServiceProvider, CancellationToken, Task>? seedDemoData = null)
    {
        if (args.Length > 0 && args[0] == CodegenCommand)
        {
            // `<Service>.Api codegen write|test|preview|delete`: JasperFx's code generation commands (the WriteGeneratedCode build
            // target runs `codegen write`, docs/generated-code.md). The host is built, never started.
            Environment.ExitCode = await app.RunJasperFxCommands(args);
            return;
        }

        if (!args.Contains(MigrateFlag))
        {
            await app.RunAsync();
            return;
        }

        var cancellationToken = app.Lifetime.ApplicationStopping;
        var service = app.Configuration.GetSection(ServiceOptions.SectionName).Get<ServiceOptions>();
        var store = service?.Store ?? ServiceStore.Postgres;
        var mongo = store == ServiceStore.MongoDB || !string.IsNullOrWhiteSpace(service?.SecondaryMongoConnectionName);
        if (mongo)
        {
            // A MongoDB service has no message store: its migrations are the registered IMongoMigrations (indexes). A Postgres service
            // with a second database on MongoDB runs them too, before its EF Core migrations and message store.
            var database = app.Services.GetRequiredService<IMongoDatabase>();
            foreach (var step in app.Services.GetServices<IMongoMigration>())
            {
                await step.ApplyAsync(database, cancellationToken);
            }
        }

        if (migrate is not null)
        {
            await migrate(app.Services, cancellationToken);
        }

        if (store == ServiceStore.Postgres)
        {
            await app.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.MigrateAsync();
        }

        if (seedDemoData is not null && app.Configuration.GetValue<bool>(DemoSeedSetting))
        {
            await seedDemoData(app.Services, cancellationToken);
        }
    }

    /// <summary>True while the <c>Microsoft.Extensions.ApiDescription.Server</c> build step runs the app to read its OpenAPI document.</summary>
    private static bool IsWritingOpenApiDocument() =>
        Assembly.GetEntryAssembly()?.GetName().Name == OpenApiToolAssembly;

    private static string DatabaseName(ServiceOptions service) =>
        service.DatabaseConnectionName
        ?? throw new InvalidOperationException($"'{ServiceOptions.SectionName}:{nameof(ServiceOptions.DatabaseConnectionName)}' is not configured.");

    private static string Required(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name)
        ?? throw new InvalidOperationException($"Connection string '{name}' is not configured.");
}
