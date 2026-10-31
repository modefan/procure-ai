using System.Net;
using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Purchasing.Domain;
using ProcureAi.Purchasing.Infrastructure;
using Shouldly;

namespace ProcureAi.Purchasing.IntegrationTests;

/// <summary>
/// The nightly demo reset of Purchasing (plan 06 rulings 4 and 5) on a database of its own: every order, line, receipt and receipt line is
/// deleted with ordinary deletes the interceptor records, and the order numbers start again at PO-1001.
/// </summary>
[Collection(PurchasingCollection.Name)]
public sealed class DemoResetTests(PurchasingApp app)
{
    [Fact]
    public async Task Demo_reset_clears_business_rows_and_reseeds()
    {
        var database = await app.Infra.CreateMigratedDatabaseAsync($"reset_{Guid.NewGuid():N}");
        await using var factory = new PurchasingApiFactory(
            app.Infra, app.Access, app.Supplier, app.Hooks, [], database, new Dictionary<string, string?> { ["DemoReset:Enabled"] = "true" });
        var now = DateTimeOffset.UtcNow;
        var orders = new List<Guid>();
        await using (var db = NewDbContext(database))
        {
            foreach (var tenant in new[] { PurchasingApp.NewTenant(), PurchasingApp.NewTenant() })
            {
                var order = PurchaseOrder.Create(
                    tenant, Guid.NewGuid(), OrderNumber.FromSequence(1), "PR-0001", "rita", "CC-100", "contoso-hardware",
                    [new OrderLine(1, "LAP-14", "Laptop", 2, Money.Zar(100m))], now);
                order.MarkSent("SO-1", now);
                order.Confirm("SO-1", null, now);
                order.Receive([(1, 1)], "rita", null, now);
                db.Orders.Add(order);
                orders.Add(order.Id);
                await db.Database.ExecuteSqlAsync(
                    $"insert into tenant_sequences (tenant_id, name, value) values ({tenant}, {OrderNumber.SequenceName}, 1)",
                    TestContext.Current.CancellationToken);
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateClient();
        (await DemoResetProbe.ResetAsync(client, "purchasing")).ShouldBe(HttpStatusCode.NoContent);

        foreach (var table in new[] { "purchase_orders", "order_lines", "goods_receipts", "receipt_lines", "tenant_sequences" })
        {
            (await DemoResetProbe.CountAsync(database, $"select count(*) from {table}")).ShouldBe(0, table);
        }

        var deletes = await DemoResetProbe.DeleteEntriesAsync(database, "purchase_orders");
        deletes.Select(d => d.Key).ShouldBe(orders.Select(o => o.ToString()), ignoreOrder: true);
        (await DemoResetProbe.DeleteEntriesAsync(database, "receipt_lines")).Count.ShouldBe(2);
        (await DemoResetProbe.ResetAsync(client, "purchasing")).ShouldBe(HttpStatusCode.NoContent, "twice is safe");
    }

    private static PurchasingDbContext NewDbContext(string database) =>
        new(new DbContextOptionsBuilder<PurchasingDbContext>().UseNpgsql(database).Options);
}
