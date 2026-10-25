namespace ProcureAi.BuildingBlocks.Messaging.Caching;

/// <summary>
/// "Evict these cache tags", published by <see cref="MessageCacheInvalidator"/> from inside a message handler. Wolverine sends
/// it only when the handler's transaction has committed (and drops it when the handler fails), to this service's own local queue.
/// </summary>
public sealed record EvictCacheTags(string[] Tags);
