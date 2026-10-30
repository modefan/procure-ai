using Microsoft.Extensions.Configuration;
using Npgsql;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.BuildingBlocks.Application.Status;

namespace ProcureAi.Approvals.Infrastructure;

/// <summary>
/// Approvals' part of the status snapshot (plan 06 ruling 10), per tenant, read straight from the database as the system (every tenant, no
/// query filter), in one round trip:
/// <list type="bullet">
/// <item><c>sagas_running</c>: sagas in a state that still waits for something (AwaitingBudget, AwaitingApproval, Approved, Ordered, Cancelling).</item>
/// <item><c>timeouts_next_hour</c>: <see cref="StepTimeout"/> messages scheduled in Wolverine's durable store and due within the hour (reminders,
/// escalations, finance reminders), by the tenant of the saga they are for. The envelope's <c>body</c> is Wolverine's binary envelope with the
/// message's JSON inside, so the saga is found by the message's <c>RequestId</c> in that JSON (StatusContributorTests proves the match).</item>
/// <item><c>finance_alerts_open</c>: sagas whose overdue alert (<c>ApprovalAlertRaised</c>: nobody left to escalate to) is still about the open step.</item>
/// </list>
/// </summary>
public sealed class ApprovalsStatusContributor(IConfiguration configuration, TimeProvider time) : IStatusContributor
{
    public const string SectionName = "approvals";

    /// <summary>The states of a saga that is still running (Completed, Rejected and Cancelled are ends).</summary>
    public static IReadOnlyList<string> RunningStates { get; } =
    [
        nameof(ApprovalState.AwaitingBudget), nameof(ApprovalState.AwaitingApproval), nameof(ApprovalState.Approved),
        nameof(ApprovalState.Ordered), nameof(ApprovalState.Cancelling),
    ];

    private const string Sql = """
        SELECT tenant_id, 'r', count(*) FROM approval_sagas WHERE state = ANY(@running) GROUP BY tenant_id
        UNION ALL
        SELECT s.tenant_id, 'a', count(*) FROM approval_sagas s
          JOIN approval_steps st ON st.id = s.overdue_alert_step_id AND st.status = 'Pending'
          WHERE s.state = 'AwaitingApproval' GROUP BY s.tenant_id
        UNION ALL
        SELECT s.tenant_id, 't', count(*) FROM wolverine.wolverine_incoming_envelopes e
          JOIN approval_sagas s ON s.id::text = lower(substring(encode(e.body, 'escape') FROM '(?i)"requestId"\s*:\s*"([0-9a-f-]{36})"'))
          WHERE e.status = 'Scheduled' AND e.message_type = @timeout AND e.execution_time <= @until
          GROUP BY s.tenant_id
        """;

    /// <summary>The kind of the alert items (the status screen maps it to a label).</summary>
    public const string FinanceOverdueKind = "finance-overdue";

    /// <summary>At most this many open finance alerts per tenant are listed (the counter holds the full number).</summary>
    public const int LatestPerTenant = 20;

    private const string AlertsSql = """
        SELECT tenant_id, id, request_number, due_at FROM (
          SELECT s.tenant_id, s.id, s.request_number, st.due_at,
                 row_number() OVER (PARTITION BY s.tenant_id ORDER BY st.due_at DESC) AS rn
          FROM approval_sagas s
          JOIN approval_steps st ON st.id = s.overdue_alert_step_id AND st.status = 'Pending'
          WHERE s.state = 'AwaitingApproval') x
        WHERE rn <= @limit ORDER BY due_at DESC
        """;

    public string Name => SectionName;

    public async Task<StatusSection> CollectAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString(DependencyInjection.DatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string {DependencyInjection.DatabaseConnectionName} is not configured.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("running", RunningStates.ToArray());
        command.Parameters.AddWithValue("timeout", StepTimeoutMessageType);
        command.Parameters.AddWithValue("until", time.GetUtcNow().AddHours(1));

        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(1) switch
            {
                "r" => StatusCounters.SagasRunning,
                "a" => StatusCounters.FinanceAlertsOpen,
                _ => StatusCounters.TimeoutsNextHour,
            };
            counters[StatusCounters.Key(reader.GetString(0), name)] = reader.GetInt64(2);
        }

        await reader.CloseAsync();

        // One item per open finance alert: the step nobody is left to escalate has been waiting since its due time.
        var items = new List<StatusItem>();
        await using var alerts = new NpgsqlCommand(AlertsSql, connection);
        alerts.Parameters.AddWithValue("limit", LatestPerTenant);
        await using var alertReader = await alerts.ExecuteReaderAsync(ct);
        while (await alertReader.ReadAsync(ct))
        {
            var due = alertReader.GetFieldValue<DateTimeOffset>(3);
            items.Add(new StatusItem(
                FinanceOverdueKind,
                $"{alertReader.GetString(2)}: finance step overdue since {due.UtcDateTime:yyyy-MM-dd HH:mm}",
                alertReader.GetString(0), due, "/app/requests/" + alertReader.GetGuid(1)));
        }

        return new StatusSection(SectionName, counters, items);
    }

    /// <summary>How Wolverine names <see cref="StepTimeout"/> in its envelopes (the full type name, no alias is set).</summary>
    public static string StepTimeoutMessageType => typeof(StepTimeout).FullName!;
}
