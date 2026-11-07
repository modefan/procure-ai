namespace ProcureAi.Assistant.Application.Chat;

/// <summary>Where a tenant stands against the daily token cap: tokens used today, the cap and when the day ends.</summary>
/// <param name="ResetsAt">When the day ends (South African midnight), for <c>Retry-After</c>.</param>
public sealed record TokenBudget(long Used, long Cap, DateTimeOffset ResetsAt)
{
    /// <summary>Whether the cap is reached: no further answers today.</summary>
    public bool Spent => Used >= Cap;
}

/// <summary>
/// The tokens per tenant per calendar day of <c>Assistant:Quota:TimeZone</c> (<c>AI:DailyTokenCapPerTenant</c>), across every instance: the
/// input and output tokens of every model call made for the tenant (answers, query rewrites, the guard classifier, invoice scans, chunk
/// context and embeddings during ingestion) are added by each pipeline's counting stage once the call has reported them. Questions,
/// uploads, replacements and scans are refused once the day's total has reached the cap. The check comes before the work and the count
/// after it, so every call already in flight for the tenant (all its users, on every instance) finishes and is counted: the cap can be
/// passed by that much. Only the per-user rate limits, the per-conversation lock and the per-call output limits bound the overshoot.
/// </summary>
public interface ITenantTokenBudget
{
    Task<TokenBudget> GetAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>Adds <paramref name="tokens"/> to today's total of the tenant (nothing for zero or less).</summary>
    Task AddAsync(string tenantId, long tokens, CancellationToken cancellationToken);
}
