namespace ProcureAi.Assistant.Application.Chat;

/// <summary>Where a tenant stands against the daily token cap: tokens used today, the cap and when the day ends.</summary>
/// <param name="ResetsAt">When the day ends (South African midnight), for <c>Retry-After</c>.</param>
public sealed record TokenBudget(long Used, long Cap, DateTimeOffset ResetsAt)
{
    /// <summary>Whether the cap is reached: no further answers today.</summary>
    public bool Spent => Used >= Cap;
}

/// <summary>A reservation of tokens on one tenant's total of one day, made before a model call and settled after it.</summary>
public sealed record TokenReservation(string TenantId, DateOnly Day, long Tokens);

/// <summary>
/// The tokens per tenant per calendar day of <c>Assistant:Quota:TimeZone</c> (<c>AI:DailyTokenCapPerTenant</c>), across every instance: the
/// input and output tokens of every model call made for the tenant (answers, query rewrites, the guard classifier, invoice scans, chunk
/// context and embeddings during ingestion). Each pipeline's counting stage <see cref="ReserveAsync">reserves</see> an estimate before the
/// call (the prompt size plus the call's output limit, <c>AI:CharsPerTokenEstimate</c>) and <see cref="SettleAsync">settles</see> it with
/// the usage the provider reports afterwards. A call that never reports usage (a client that left mid-stream, a provider error after the
/// request was sent) keeps its reservation, so a cancelled answer is still charged. Questions, uploads, replacements and scans are refused
/// once the day's total has reached the cap (a question also right before its answer call). Calls in flight are already reserved, so a new
/// check sees them, but work that passed its check before the cap was reached still runs to the end: a scripted burst of questions fired
/// together at the cap can add about USD 1-2 once, bounded by the per-user quota and the deployment's rate limit.
/// </summary>
public interface ITenantTokenBudget
{
    Task<TokenBudget> GetAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>Adds <paramref name="tokens"/> to today's total of the tenant (nothing for zero or less).</summary>
    Task AddAsync(string tenantId, long tokens, CancellationToken cancellationToken);

    /// <summary>Adds <paramref name="tokens"/> to today's total of the tenant before a model call, to be settled after it.</summary>
    Task<TokenReservation> ReserveAsync(string tenantId, long tokens, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the reservation by the call's real usage on the reservation's own day (adds the difference, which may be negative).
    /// </summary>
    Task SettleAsync(TokenReservation reservation, long usedTokens, CancellationToken cancellationToken);
}
