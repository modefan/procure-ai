using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Access.Infrastructure;
using ProcureAi.Approvals.Infrastructure;
using ProcureAi.Audit.Application;
using ProcureAi.Budgets.Infrastructure;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Requests.Infrastructure;
using Shouldly;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// Debezium's messages carry the row, not which columns are its key, so Audit knows the key columns of every table from
/// configuration (<c>Audit:Cdc</c>) and builds the key text the interceptor writes (key values joined by a comma, in key order).
/// This test keeps that configuration equal to the services' EF models: a new table or a changed key fails here, not silently
/// in production as "changed outside the app".
/// </summary>
[Collection(AuditCollection.Name)]
public sealed class CdcKeyColumnsTests(AuditApp app)
{
    public static TheoryData<string> Services => ["requests", "budgets", "access", "approvals"];

    [Theory]
    [MemberData(nameof(Services))]
    public void Key_columns_configuration_matches_the_services_models(string service)
    {
        var cdc = app.Services.GetRequiredService<IOptions<AuditOptions>>().Value.Cdc;
        using var db = Model(service);

        // Every table of the model with a key of its own, owned collections in a table of their own (budget_reservations) included.
        var tables = db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is { } table && e.FindPrimaryKey() is not null
                && e.ClrType != typeof(AuditEntry) && !table.StartsWith("wolverine", StringComparison.Ordinal))
            .GroupBy(e => e.GetTableName()!)
            .ToList();
        tables.ShouldNotBeEmpty();
        foreach (var table in tables)
        {
            var expected = table.First().FindPrimaryKey()!.Properties.Select(p => p.GetColumnName()).ToArray();
            cdc.KeyColumnsOf(table.Key).ShouldBe(expected, $"{service}: Audit:Cdc:KeyColumns for '{table.Key}'");
        }
    }

    private static DbContext Model(string service) => service switch
    {
        "requests" => new RequestsDbContext(new DbContextOptionsBuilder<RequestsDbContext>().UseNpgsql("Host=model-only").Options),
        "budgets" => new BudgetsDbContext(new DbContextOptionsBuilder<BudgetsDbContext>().UseNpgsql("Host=model-only").Options),
        "access" => new AccessDbContext(new DbContextOptionsBuilder<AccessDbContext>().UseNpgsql("Host=model-only").Options),
        "approvals" => new ApprovalsDbContext(new DbContextOptionsBuilder<ApprovalsDbContext>().UseNpgsql("Host=model-only").Options),
        _ => throw new ArgumentOutOfRangeException(nameof(service)),
    };
}
