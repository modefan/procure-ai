using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Purchasing.Application;
using ProcureAi.Purchasing.Application.Mapping;
using ProcureAi.Purchasing.Domain;
using Shouldly;

namespace ProcureAi.Purchasing.Tests;

public sealed class MappingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static PurchaseOrder NewOrder() => PurchaseOrder.Create(
        "acme", Guid.NewGuid(), OrderNumber.FromSequence(1), "PR-0001", "alice", "CC-100", "sup-1",
        [new OrderLine(1, "LAP-14", "Laptop 14 inch", 3, Money.Zar(25_200m))], Now);

    [Fact]
    public void The_created_event_carries_number_lines_and_totals()
    {
        var order = NewOrder();

        var created = PurchasingMapper.ToCreatedEvent(order.DomainEvents.OfType<PurchaseOrderCreatedDomainEvent>().Single());

        created.OrderNumber.ShouldBe("PO-1001");
        created.RequestNumber.ShouldBe("PR-0001");
        created.TotalExclVat.ShouldBe(75_600m);
        created.Vat.ShouldBe(11_340m);
        created.TotalInclVat.ShouldBe(86_940m);
        created.Lines.ShouldHaveSingleItem().UnitPriceExclVat.ShouldBe(25_200m);
    }

    [Fact]
    public void The_dto_shows_ordered_received_closed_and_outstanding_with_display_strings()
    {
        var order = NewOrder();
        order.Confirm("SUP-1", null, Now);
        order.Receive([(1, 2)], "bob", null, Now);

        var dto = PurchasingMapper.ToDto(order);

        dto.Number.ShouldBe("PO-1001");
        dto.TotalInclVatDisplay.ShouldBe("R 86 940,00");
        var line = dto.Lines.ShouldHaveSingleItem();
        (line.Ordered, line.Received, line.ClosedShort, line.Outstanding).ShouldBe((3, 2, 0, 1));
        line.UnitPriceExclVatDisplay.ShouldBe("R 25 200,00");
        dto.Receipts.ShouldHaveSingleItem().Lines.ShouldHaveSingleItem().Quantity.ShouldBe(2);
    }

    [Fact]
    public void Supplier_status_text_follows_the_order()
    {
        var order = NewOrder();
        SupplierStatusText.For(order).ShouldBe("Waiting for supplier");

        order.RecordSupplierFailure("timeout", Now.AddSeconds(5));
        SupplierStatusText.For(order).ShouldBe("Supplier unavailable · retrying (attempt 2)");
        var failed = PurchasingMapper.ToDto(order);
        failed.SupplierAttempts.ShouldBe(1);
        failed.SupplierLastError.ShouldBe("timeout");

        order.Confirm("SUP-1", new DateOnly(2026, 11, 3), Now);
        SupplierStatusText.For(order).ShouldBe("Confirmed, delivery 2026-11-03");
    }

    [Fact]
    public void Supplier_status_text_for_sent_rejected_and_cancelled()
    {
        var sent = NewOrder();
        sent.MarkSent("SUP-1", Now);
        SupplierStatusText.For(sent).ShouldBe("Sent to supplier, waiting for confirmation");

        var rejected = NewOrder();
        rejected.RejectBySupplier("out of stock", Now);
        SupplierStatusText.For(rejected).ShouldBe("Rejected by supplier: out of stock");

        sent.Cancel("x", Now);
        SupplierStatusText.For(sent).ShouldBe("Cancelled, telling the supplier");
    }

    [Fact]
    public void The_tombstone_maps_without_a_number()
    {
        var tombstone = PurchaseOrder.CancelledBeforeCreation("acme", Guid.NewGuid(), "x", Now);

        PurchasingMapper.NumberText(tombstone).ShouldBe("");
    }

    [Fact]
    public void Supplier_delivery_backoff_doubles_to_the_cap()
    {
        var backoff = new BackoffOptions
        {
            InitialDelay = TimeSpan.FromSeconds(5), Multiplier = 2.0, MaxDelay = TimeSpan.FromMinutes(5), JitterFraction = 0.5,
        };
        var options = new PurchasingOptions { SupplierDelivery = backoff };

        options.SupplierDelivery.BaseDelayFor(1).ShouldBe(TimeSpan.FromSeconds(5));
        options.SupplierDelivery.BaseDelayFor(3).ShouldBe(TimeSpan.FromSeconds(20));
        options.SupplierDelivery.BaseDelayFor(20).ShouldBe(TimeSpan.FromMinutes(5));
    }
}
