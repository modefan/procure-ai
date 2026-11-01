using System.Net;
using System.Net.Http.Headers;
using Npgsql;

namespace ProcureAi.Testing;

/// <summary>
/// Shared helpers for the services' demo reset tests (plan 06 Task 16): the reset call itself, and what the reset left in a service database
/// (the interceptor's Delete rows, Wolverine's envelopes, any count).
/// </summary>
public static class DemoResetProbe
{
    /// <summary>The service's reset path, e.g. <c>/api/v1/requests/internal/demo/reset</c>.</summary>
    public static string PathOf(string service) => $"/api/v1/{service}/internal/demo/reset";

    /// <summary>POSTs the reset with the admin key (or a bearer token when given) and returns the status.</summary>
    public static async Task<HttpStatusCode> ResetAsync(HttpClient client, string service, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, PathOf(service));
        if (bearer is null)
        {
            request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    /// <summary>The interceptor's Delete rows of <paramref name="table"/>: (primary key, transaction ID).</summary>
    public static async Task<IReadOnlyList<(string Key, long TransactionId)>> DeleteEntriesAsync(string database, string table)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "select primary_key, transaction_id from audit_entries where operation = 'Delete' and table_name = @table", connection);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(string, long)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return rows;
    }

    /// <summary>
    /// Writes one <c>audit_entries</c> row as the day's activity before a reset leaves them (an interceptor row from yesterday) and returns its
    /// ID: the reset must clear it (plan 07 Task 0b, carry-over 5).
    /// </summary>
    public static async Task<Guid> InsertEarlierAuditEntryAsync(string database)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into audit_entries (id, tenant_id, user_id, actor_id, table_name, primary_key, operation, changes, occurred_at)
            values (@id, 'acme', 'earlier-user', 'earlier-user', 'earlier_activity', @key, 'Insert', '{}'::jsonb, now() - interval '1 day')
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("key", id.ToString());
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>The <c>audit_entries</c> rows written before <paramref name="before"/> (what a reset started then must have cleared).</summary>
    public static async Task<long> AuditEntriesBeforeAsync(string database, DateTimeOffset before)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from audit_entries where occurred_at < @before", connection);
        command.Parameters.AddWithValue("before", before.UtcDateTime);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A scalar count, e.g. <c>select count(*) from approval_steps</c>.</summary>
    public static async Task<long> CountAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Every envelope of Wolverine's message store in schema <paramref name="schema"/>: incoming (also scheduled), outgoing, dead letters.</summary>
    public static async Task<long> EnvelopesAsync(string database, string schema = "wolverine") =>
        await CountAsync(database, $"select count(*) from {schema}.wolverine_incoming_envelopes")
        + await CountAsync(database, $"select count(*) from {schema}.wolverine_outgoing_envelopes")
        + await CountAsync(database, $"select count(*) from {schema}.wolverine_dead_letters");
}
