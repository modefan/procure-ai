using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Persistence.Mongo;

namespace ProcureAi.Assistant.Infrastructure.Mongo;

/// <summary>
/// The Assistant's second store: MongoDB database <c>assistantchatdb</c> for conversations (plan 05 Task 5a) and invoice scans (Task 6).
/// It holds no integration events, so it needs no outbox (those are on Postgres). The host's readiness check pings it and
/// <c>--migrate</c> runs its <see cref="IMongoMigration"/>s (indexes), because <c>Service:SecondaryMongoConnectionName</c> names it.
/// Collections are tenant-scoped through <see cref="TenantCollection{T}"/> (<c>AddTenantCollection</c>), added with the tasks that use them.
/// </summary>
public static class ChatDatabase
{
    public static IServiceCollection AddAssistantChatDatabase(this IServiceCollection services, IConfiguration configuration, string connectionName) =>
        services.AddProcureAiMongo(configuration, connectionName);
}
