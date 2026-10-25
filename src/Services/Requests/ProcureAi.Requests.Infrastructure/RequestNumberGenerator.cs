using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Infrastructure;

/// <summary>
/// One counter row per tenant, bumped by a single atomic upsert. Concurrent callers of the same tenant queue on the
/// row lock, so numbers are distinct; because the upsert shares the request's transaction, a failed create gives
/// its number back (no gaps).
/// </summary>
public sealed class RequestNumberGenerator(RequestsDbContext db) : IRequestNumberGenerator
{
    private const string UpsertSql =
        """
        INSERT INTO request_number_counters (tenant_id, last_value) VALUES (@tenant, 1)
        ON CONFLICT (tenant_id) DO UPDATE SET last_value = request_number_counters.last_value + 1
        RETURNING last_value
        """;

    public async Task<RequestNumber> NextAsync(string tenantId, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = UpsertSql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            var parameter = command.CreateParameter();
            parameter.ParameterName = "tenant";
            parameter.Value = tenantId;
            command.Parameters.Add(parameter);
            var value = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
            return RequestNumber.From(value);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
