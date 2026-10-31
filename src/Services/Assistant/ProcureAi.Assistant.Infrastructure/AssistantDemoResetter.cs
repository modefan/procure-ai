using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ProcureAi.Assistant.Infrastructure.Chat;
using ProcureAi.Assistant.Infrastructure.Persistence;
using ProcureAi.Assistant.Infrastructure.Scanning;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Persistence.Mongo;
using ProcureAi.BuildingBlocks.Web.Demo;
using StackExchange.Redis;
using Wolverine.Runtime;

namespace ProcureAi.Assistant.Infrastructure;

/// <summary>
/// The Assistant part of the nightly demo reset (plan 06 ruling 4): Wolverine's envelopes (also ingestion jobs), every policy document,
/// file, chunk and past purchase of every tenant (Postgres, tracked deletes), the conversations and invoice scans (MongoDB, through
/// <see cref="TenantCollection{T}"/> so the deletes are stamped as the app's) and today's question quotas; then
/// <see cref="DemoSeeder"/> seeds the procurement policy again and queues its ingestion. The past purchases come back from the events
/// of the requests ProcureAi.DemoData creates again.
/// </summary>
public sealed class AssistantDemoResetter(
    AssistantDbContext db,
    IWolverineRuntime runtime,
    IMongoDatabase chat,
    IConnectionMultiplexer redis,
    IServiceProvider services,
    TimeProvider time,
    ILogger<AssistantDemoResetter> logger) : IDemoResetter
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await WolverineDemoReset.ClearMessageStoreAsync(runtime);
        var rows = await EfDemoReset.DeleteAllRowsAsync(db, cancellationToken: cancellationToken);
        var conversations = await MongoDemoReset.DeleteAllAsync(
            chat.GetCollection<ConversationDocument>(MongoConversationStore.CollectionName), time, cancellationToken);
        var scans = await MongoDemoReset.DeleteAllAsync(
            chat.GetCollection<InvoiceScanDocument>(MongoScanStore.CollectionName), time, cancellationToken);
        var quotas = await RedisDemoReset.DeleteKeysAsync(redis, RedisQuestionQuota.KeyPattern);
        await services.SeedDemoDataAsync(cancellationToken);
        logger.LogInformation(
            "Demo reset deleted {Rows} rows, {Conversations} conversations, {Scans} scans and {Quotas} quotas; the policy is seeded again",
            rows, conversations, scans, quotas);
    }
}
