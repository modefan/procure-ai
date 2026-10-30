using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Assistant.Application.Scanning;
using ProcureAi.BuildingBlocks.Persistence.Mongo;

namespace ProcureAi.Assistant.Infrastructure.Scanning;

public static class ScanningSetup
{
    /// <summary>
    /// Invoice scanning (plan 05 Task 6): the tenant-scoped <c>invoice_scans</c> collection of the chat database with its TTL index
    /// migration, the store and the Application's <see cref="InvoiceScanner"/>. The text extractors and the document guard come from
    /// ingestion and <c>AddAssistantAi</c>.
    /// </summary>
    public static IServiceCollection AddAssistantScanning(this IServiceCollection services)
    {
        services.AddTenantCollection<InvoiceScanDocument>(MongoScanStore.CollectionName);
        services.AddSingleton<IMongoMigration, ScanRetentionMigration>();
        services.AddScoped<IScanStore, MongoScanStore>();
        services.AddScoped<InvoiceScanner>();
        return services;
    }
}
