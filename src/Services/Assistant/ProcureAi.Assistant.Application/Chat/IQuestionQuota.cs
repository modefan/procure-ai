namespace ProcureAi.Assistant.Application.Chat;

/// <summary>Where a user stands against the daily quota: questions counted today and the limit.</summary>
/// <param name="Allowed">Whether this question was counted (false: the limit was already reached, nothing was counted).</param>
/// <param name="Used">Questions counted today, this one included when it was allowed.</param>
/// <param name="ResetsAt">When the day ends (South African midnight), for <c>Retry-After</c>.</param>
public sealed record QuotaCount(bool Allowed, int Used, int Limit, DateTimeOffset ResetsAt);

/// <summary>
/// The questions per user per South African calendar day (ruling 7, <c>Assistant:Quota:QuestionsPerDay</c>, <c>Assistant:Quota:TimeZone</c>),
/// counted per tenant and user across every instance. A question counts when its answer starts, and also when a guard blocks it.
/// </summary>
public interface IQuestionQuota
{
    /// <summary>Counts one question unless the limit is reached (atomically: two questions at once never both take the last one).</summary>
    Task<QuotaCount> TryCountAsync(string tenantId, string userId, CancellationToken cancellationToken);

    /// <summary>Today's count, without counting.</summary>
    Task<QuotaCount> GetAsync(string tenantId, string userId, CancellationToken cancellationToken);
}
