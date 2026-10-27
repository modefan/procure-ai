using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Access.Infrastructure;
using ProcureAi.Approvals.Infrastructure;
using ProcureAi.Audit.Application;
using ProcureAi.Budgets.Infrastructure;
using ProcureAi.Invoicing.Infrastructure;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Purchasing.Infrastructure;
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
    public static TheoryData<string> Services => ["requests", "budgets", "access", "approvals", "purchasing", "invoicing"];

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

    [Fact]
    public void Every_configured_key_column_table_exists_in_a_services_model()
    {
        var cdc = app.Services.GetRequiredService<IOptions<AuditOptions>>().Value.Cdc;
        var modelTables = new[] { "requests", "budgets", "access", "approvals", "purchasing", "invoicing" }.SelectMany(service =>
        {
            using var db = Model(service);
            return db.Model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().ToList();
        }).ToHashSet(StringComparer.Ordinal);

        cdc.KeyColumns.Keys.Where(table => !modelTables.Contains(table)).ShouldBeEmpty("stale Audit:Cdc:KeyColumns entries (no such table in any model)");
    }

    private static DbContext Model(string service) => service switch
    {
        "requests" => new RequestsDbContext(new DbContextOptionsBuilder<RequestsDbContext>().UseNpgsql("Host=model-only").Options),
        "budgets" => new BudgetsDbContext(new DbContextOptionsBuilder<BudgetsDbContext>().UseNpgsql("Host=model-only").Options),
        "access" => new AccessDbContext(new DbContextOptionsBuilder<AccessDbContext>().UseNpgsql("Host=model-only").Options),
        "approvals" => new ApprovalsDbContext(new DbContextOptionsBuilder<ApprovalsDbContext>().UseNpgsql("Host=model-only").Options),
        "purchasing" => new PurchasingDbContext(new DbContextOptionsBuilder<PurchasingDbContext>().UseNpgsql("Host=model-only").Options),
        "invoicing" => new InvoicingDbContext(new DbContextOptionsBuilder<InvoicingDbContext>().UseNpgsql("Host=model-only").Options),
        _ => throw new ArgumentOutOfRangeException(nameof(service)),
    };
}
