using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using ProcureAi.BuildingBlocks.Web.Status;

namespace ProcureAi.Assistant.Infrastructure.Chat;

public static class ChatSetup
{
    /// <summary>
    /// Conversations and answers (plan 05 Task 5a): the tenant-scoped <c>conversations</c> collection of the chat database and its index
    /// migration, the store, the Redis answer lock and question quota (on the shared <c>IConnectionMultiplexer</c>), the caller's runtime permissions
    /// (<see cref="ICallerPermissions"/>: past purchases need <c>requests.read</c>), and the Application's
    /// <see cref="AnswerStreamer"/>, <see cref="QueryRewriter"/> and <see cref="ConversationService"/>.
    /// </summary>
    public static IServiceCollection AddAssistantChat(this IServiceCollection services)
    {
        services.AddTenantCollection<ConversationDocument>(MongoConversationStore.CollectionName);
        services.AddSingleton<IMongoMigration, ConversationIndexMigration>();
        services.AddScoped<IConversationStore, MongoConversationStore>();
        services.AddSingleton<IAnswerLock, RedisAnswerLock>();
        services.AddSingleton<IQuestionQuota, RedisQuestionQuota>();
        services.AddScoped<QueryRewriter>();
        services.AddScoped<ICallerPermissions, CallerPermissions>();
        services.AddScoped<AnswerStreamer>();
        services.AddScoped<ConversationService>();
        services.AddStatusContributor<AssistantStatusContributor>();
        return services;
    }
}
