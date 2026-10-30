using Marten;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application.Status;
using Shouldly;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>Audit's part of the system status snapshot (plan 06 ruling 10): the open outside-the-app alerts, per tenant, newest first.</summary>
[Collection(AuditCollection.Name)]
public sealed class StatusContributorTests(AuditApp app)
{
    [Fact]
    public async Task Audit_lists_open_outside_app_alerts()
    {
        var acme = AuditApp.NewTenant();
        var globex = AuditApp.NewTenant();
        var now = DateTimeOffset.UtcNow;
        var store = app.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            // 22 open alerts of Acme (only the newest 20 are listed), one withdrawn (the app's row came late), one of Globex.
            for (var i = 0; i < 22; i++)
            {
                session.Store(Alert(acme, $"purchase_orders-{i}", now.AddMinutes(-i)));
            }

            session.Store(Alert(acme, "withdrawn", now.AddMinutes(1)) with { WithdrawnAt = now.AddMinutes(2) });
            session.Store(Alert(globex, "globex-row", now));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = app.Services.CreateAsyncScope();
        var contributor = scope.ServiceProvider.GetServices<IStatusContributor>().Single(c => c.Name == "audit");
        var section = await contributor.CollectAsync(TestContext.Current.CancellationToken);

        section.Counters[StatusCounters.Key(acme, StatusCounters.OutsideAppAlertsOpen)].ShouldBe(22, "a withdrawn alert is not open");
        section.Counters[StatusCounters.Key(globex, StatusCounters.OutsideAppAlertsOpen)].ShouldBe(1);
        var acmeItems = section.Items.Where(i => i.TenantId == acme).ToList();
        acmeItems.Count.ShouldBe(20, "the newest 20 per tenant");
        acmeItems.Select(i => i.At).ShouldBeInOrder(SortDirection.Descending);
        acmeItems[0].At.ShouldBe(now, TimeSpan.FromMilliseconds(1));
        acmeItems[0].Kind.ShouldBe("outside-app-change");
        acmeItems[0].Text.ShouldContain("purchase_orders");
        acmeItems[0].Link.ShouldBe("/app/admin/data-changes?outside=true&table=purchase_orders");
        acmeItems.ShouldNotContain(i => i.Text.Contains("withdrawn"));
        section.Items.Where(i => i.TenantId == globex).ShouldHaveSingleItem().Text.ShouldContain("globex-row");
    }

    private static OutsideAppAlert Alert(string tenant, string key, DateTimeOffset committedAt) =>
        new($"test-{Guid.NewGuid():N}", "purchasingdb", "purchase_orders", key, tenant, 4711, committedAt, committedAt, null);
}
