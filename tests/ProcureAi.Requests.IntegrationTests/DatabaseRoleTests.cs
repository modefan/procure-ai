namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// Plan 08 ruling 6: the Requests database as deployed, from infra/postgres/roles.sql. The suite's service migrated and runs as its own role
/// <c>requests_app</c>, which owns everything its migration made and is refused by every other service database (see
/// <see cref="DatabaseRoleChecks"/>).
/// </summary>
[Collection(InfraCollection.Name)]
public sealed class DatabaseRoleTests(InfraFixture infra)
{
    [Fact]
    public Task Service_role_owns_its_database_and_cannot_read_another_database() =>
        DatabaseRoleChecks.ServiceRoleOwnsItsDatabaseAndCannotReadAnotherAsync(infra.Postgres, "requests", infra.RequestsDbConnectionString);

    /// <summary>The demo reset empties <c>audit_entries</c> with <c>TRUNCATE</c>: the service's own role keeps that right.</summary>
    [Fact]
    public Task Service_role_can_truncate_its_audit_entries() => DatabaseRoleChecks.ServiceRoleCanTruncateItsAuditEntriesAsync(infra.RequestsDbConnectionString);

    [Fact]
    public Task Cdc_role_reads_only_the_published_tables() => DatabaseRoleChecks.CdcRoleReadsOnlyThePublishedTablesAsync(infra.Postgres, "requests");
}
