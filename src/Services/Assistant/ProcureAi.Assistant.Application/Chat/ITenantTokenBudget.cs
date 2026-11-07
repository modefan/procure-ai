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
/// input and output tokens of each answer and its query rewrite are added when they are known. A question is refused once the day's
/// total has reached the cap; the answer that crosses it is finished, so the cap may be passed by at most one answer.
/// </summary>
public interface ITenantTokenBudget
{
    Task<TokenBudget> GetAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>Adds <paramref name="tokens"/> to today's total of the tenant (nothing for zero or less).</summary>
    Task AddAsync(string tenantId, long tokens, CancellationToken cancellationToken);
}
