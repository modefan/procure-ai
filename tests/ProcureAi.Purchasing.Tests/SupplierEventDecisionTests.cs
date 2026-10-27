using System.ComponentModel.DataAnnotations;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Purchasing.Application;
using ProcureAi.Purchasing.Application.Commands;
using ProcureAi.Purchasing.Domain;
using Shouldly;

namespace ProcureAi.Purchasing.Tests;

public sealed class SupplierEventDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static PurchaseOrder NewOrder() => PurchaseOrder.Create(
        "acme", Guid.NewGuid(), OrderNumber.FromSequence(1), "PR-0001", "alice", "CC-100", "sup-1",
        [new OrderLine(1, "LAP-14", "Laptop", 3, Money.Zar(25_200m))], Now);

    private static ApplySupplierOrderEvent Confirmed(PurchaseOrder o, string id = "e1") =>
        new(id, SupplierOrderEventKind.Confirmed, o.Id, "SUP-1", new DateOnly(2026, 11, 3), null);

    private static ApplySupplierOrderEvent Declined(PurchaseOrder o) =>
        new("e2", SupplierOrderEventKind.Declined, o.Id, "SUP-1", null, "out of stock");

    [Fact]
    public void Confirmation_publishes_the_confirmed_event_once()
    {
        var order = NewOrder();

        var decision = SupplierEventDecisions.Apply(order, Confirmed(order), Now);

        decision.Event.ShouldBeOfType<PurchaseOrderConfirmed>().SupplierReference.ShouldBe("SUP-1");
        decision.CancelAtSupplier.ShouldBeFalse();
        SupplierEventDecisions.Apply(order, Confirmed(order, "e3"), Now).Event.ShouldBeNull();
    }

    [Fact]
    public void Confirmation_after_cancel_without_reference_cancels_once_at_the_supplier()
    {
        var order = NewOrder();
        order.RecordSupplierFailure("timeout", Now);
        order.Cancel("x", Now);

        var first = SupplierEventDecisions.Apply(order, Confirmed(order), Now);
        var second = SupplierEventDecisions.Apply(order, Confirmed(order, "e9"), Now);

        first.CancelAtSupplier.ShouldBeTrue();
        first.Event.ShouldBeNull();
        order.SupplierReference.ShouldBe("SUP-1");
        order.SupplierCancelPending.ShouldBeTrue();
        second.CancelAtSupplier.ShouldBeFalse();
        second.Event.ShouldBeNull();
    }

    [Fact]
    public void Decline_after_cancel_is_ignored_silently()
    {
        var order = NewOrder();
        order.Cancel("x", Now);

        var decision = SupplierEventDecisions.Apply(order, Declined(order), Now);

        decision.ShouldBe(new SupplierEventDecision(null, false, null));
        order.SupplierCancelPending.ShouldBeFalse();
    }

    [Fact]
    public void Decline_publishes_rejected_and_events_that_break_a_rule_are_reported_ignored()
    {
        var order = NewOrder();
        SupplierEventDecisions.Apply(order, Declined(order), Now).Event.ShouldBeOfType<PurchaseOrderRejected>().Reason.ShouldBe("out of stock");

        var confirmed = NewOrder();
        confirmed.Confirm("SUP-1", null, Now);
        SupplierEventDecisions.Apply(confirmed, Declined(confirmed), Now).Ignored.ShouldNotBeNull();
        SupplierEventDecisions.Apply(order, Confirmed(order), Now).Ignored.ShouldNotBeNull();
    }

    [Fact]
    public void Supplier_delivery_values_match_the_brief_and_are_validated()
    {
        var options = new PurchasingOptions
        {
            SupplierDelivery = new()
            {
                InitialDelay = TimeSpan.Parse("00:00:05"), Multiplier = 2.0, MaxDelay = TimeSpan.Parse("00:05:00"), JitterFraction = 0.5,
            },
        };
        options.SupplierDelivery.InitialDelay.ShouldBe(TimeSpan.FromSeconds(5));
        options.SupplierDelivery.Multiplier.ShouldBe(2.0);
        options.SupplierDelivery.MaxDelay.ShouldBe(TimeSpan.FromMinutes(5));
        options.SupplierDelivery.JitterFraction.ShouldBe(0.5);
        Validate(options.SupplierDelivery).ShouldBeTrue();

        Validate(new BackoffOptions { InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.FromMinutes(5) }).ShouldBeFalse();
        Validate(new BackoffOptions { InitialDelay = TimeSpan.FromSeconds(5), MaxDelay = TimeSpan.FromMinutes(5), JitterFraction = 2 }).ShouldBeFalse();
    }

    private static bool Validate(object instance) =>
        Validator.TryValidateObject(instance, new ValidationContext(instance), [], validateAllProperties: true);
}
