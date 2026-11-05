using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using ProcureAi.Suppliers.Application.Abstractions;
using ProcureAi.Suppliers.Application.Queries;
using Wolverine;

namespace ProcureAi.Suppliers.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseConnectionName = "suppliersdb";
    public const string SuppliersCollection = "suppliers";

    /// <summary>
    /// The MongoDB client and database, the tenant-scoped collection, the store and the migration <c>--migrate</c> runs (the index). It runs as
    /// Suppliers' own user <c>suppliers_app</c> (readWrite on suppliersdb only, plan 08 Task 4); the users, debezium-mongo's included, come
    /// from infra/mongo/users.js.
    /// The caller registers <see cref="BuildingBlocks.Application.ICallerContext"/> and the <c>Paging</c> options.
    /// </summary>
    public static IServiceCollection AddSuppliersInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddProcureAiMongo(configuration, DatabaseConnectionName);
        services.AddTenantCollection<SupplierDocument>(SuppliersCollection);
        services.AddScoped<ISupplierStore, MongoSupplierStore>();
        services.AddSingleton<IMongoMigration, SupplierIndexMigration>();
        services.AddScoped<ProcureAi.BuildingBlocks.Web.Demo.IDemoResetter, SuppliersDemoResetter>();
        return services;
    }

    /// <summary>
    /// Wolverine without a message store: Suppliers publishes nothing and consumes only <c>AccessChanged</c> (to evict cached
    /// permission answers), on a buffered queue. Its query handlers run in-process.
    /// </summary>
    public static void ConfigureSuppliers(this WolverineOptions options, IConfiguration configuration, string serviceName)
    {
        options.UseProcureAiAccessEviction(configuration, serviceName, typeof(ListSuppliersHandler).Assembly);
        // The store is built from the tenant-scoped collection, which is registered with a factory (it needs the caller of the
        // request), so Wolverine resolves the store from the container instead of generating code to construct it.
        options.CodeGeneration.AlwaysUseServiceLocationFor<ISupplierStore>();
    }
}
