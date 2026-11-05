namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// Plan 08 ruling 6: the Audit database as deployed, from infra/postgres/roles.sql. The suite's service migrated and runs as its own role
/// <c>audit_app</c>, which owns everything its migration made and is refused by every other service database (see
/// <see cref="DatabaseRoleChecks"/>).
/// </summary>
[Collection(AuditCollection.Name)]
public sealed class DatabaseRoleTests(AuditApp app)
{
    [Fact]
    public Task Service_role_owns_its_database_and_cannot_read_another_database() =>
        DatabaseRoleChecks.ServiceRoleOwnsItsDatabaseAndCannotReadAnotherAsync(app.Infra.Postgres, "audit", app.Infra.DbConnectionString);
}
