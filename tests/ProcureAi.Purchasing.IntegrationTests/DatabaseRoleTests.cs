namespace ProcureAi.Purchasing.IntegrationTests;

/// <summary>
/// Plan 08 ruling 6: the Purchasing database as deployed, from infra/postgres/roles.sql. The suite's service migrated and runs as its own role
/// <c>purchasing_app</c>, which owns everything its migration made and is refused by every other service database (see
/// <see cref="DatabaseRoleChecks"/>).
/// </summary>
[Collection(PurchasingCollection.Name)]
public sealed class DatabaseRoleTests(PurchasingApp app)
{
    [Fact]
    public Task Service_role_owns_its_database_and_cannot_read_another_database() =>
        DatabaseRoleChecks.ServiceRoleOwnsItsDatabaseAndCannotReadAnotherAsync(app.Infra.Postgres, "purchasing", app.Infra.DbConnectionString);

    /// <summary>The demo reset empties <c>audit_entries</c> with <c>TRUNCATE</c>: the service's own role keeps that right.</summary>
    [Fact]
    public Task Service_role_can_truncate_its_audit_entries() => DatabaseRoleChecks.ServiceRoleCanTruncateItsAuditEntriesAsync(app.Infra.DbConnectionString);

    [Fact]
    public Task Cdc_role_reads_only_the_published_tables() => DatabaseRoleChecks.CdcRoleReadsOnlyThePublishedTablesAsync(app.Infra.Postgres, "purchasing");
}
