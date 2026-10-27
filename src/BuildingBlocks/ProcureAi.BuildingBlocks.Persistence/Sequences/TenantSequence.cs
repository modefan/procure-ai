using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>
/// One row per tenant and name, bumped by a single atomic upsert. Concurrent callers of the same sequence queue on the row
/// lock, so numbers are distinct; the upsert shares the caller's transaction, so a failed save gives its number back.
/// </summary>
public sealed class TenantSequence<TDbContext>(TDbContext db) : ITenantSequence
    where TDbContext : DbContext
{
    private const string UpsertSql =
        """
        INSERT INTO tenant_sequences (tenant_id, name, value) VALUES (@tenant, @name, 1)
        ON CONFLICT (tenant_id, name) DO UPDATE SET value = tenant_sequences.value + 1
        RETURNING value
        """;

    public async Task<int> NextAsync(string tenantId, string name, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = UpsertSql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            AddParameter(command, "tenant", tenantId);
            AddParameter(command, "name", name);
            return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
