using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Application.Commands;
using ProcureAi.Invoicing.Domain;
using Shouldly;

namespace ProcureAi.Invoicing.Tests;

/// <summary>A webhook invoice for an order Invoicing does not know yet has no tenant until the order arrives (never the payload's).</summary>
public sealed class WaitingInvoiceTests
{
    private static Invoice Waiting(string number = "INV-W") => Invoice.CreateAwaitingOrder(
        Fixture.OrderId, "sup-1", number, new DateOnly(2026, 10, 27), [new InvoiceLine(1, "", "", 3, Money.Zar(25_200m))],
        Money.Zar(86_940m), "{}", Fixture.Now);

    [Fact]
    public void A_waiting_invoice_has_no_tenant_and_no_arrival_number()
    {
        var invoice = Waiting();

        invoice.HasTenant.ShouldBeFalse();
        invoice.TenantId.ShouldBe(Invoice.UnassignedTenant);
        invoice.Sequence.ShouldBe(0);
        invoice.Status.ShouldBe(InvoiceStatus.AwaitingOrder);
        invoice.Source.ShouldBe(InvoiceSource.Webhook);
    }

    [Fact]
    public void It_cannot_be_matched_before_it_has_the_tenant_of_the_order()
    {
        var invoice = Waiting();

        Should.Throw<DomainException>(() => MatchDecisions.Rematch(Fixture.Order(3), [invoice], Fixture.Tolerance));
    }

    [Fact]
    public void Given_the_order_tenant_it_is_matched_like_any_invoice()
    {
        var invoice = Waiting();
        var order = Fixture.Order(3);
        var revision = invoice.Revision;

        invoice.AssignTenant(order.TenantId, 7);
        var outcome = MatchDecisions.Rematch(order, [invoice], Fixture.Tolerance).ShouldHaveSingleItem();

        invoice.Revision.ShouldBeGreaterThan(revision);
        (invoice.TenantId, invoice.Sequence).ShouldBe(("acme", 7L));
        outcome.WasAwaitingOrder.ShouldBeTrue();
        invoice.Status.ShouldBe(InvoiceStatus.Matched);
        invoice.Lines.Single().Sku.ShouldBe("LAP-14", "the SKU the webhook left out comes from the order");
    }

    [Fact]
    public void The_tenant_is_given_once()
    {
        var invoice = Waiting();
        invoice.AssignTenant("acme", 1);

        Should.Throw<DomainException>(() => invoice.AssignTenant("other", 2));
        Should.Throw<DomainException>(() => Waiting().AssignTenant("acme", 0));
    }

    [Fact]
    public void An_invoice_of_one_tenant_never_attaches_to_an_order_of_another()
    {
        var invoice = Fixture.Invoice();
        var order = OrderView.Create(
            Fixture.OrderId, "intruder", Guid.NewGuid(), "PO-1001", "sup-1", "CC-100", "alice",
            [new OrderViewLine(1, "LAP-14", "Laptop", 3, Money.Zar(25_200m))]);

        Should.Throw<DomainException>(() => invoice.AttachOrder(order));
    }

    [Fact]
    public void An_invoice_of_a_tenant_needs_its_arrival_number() =>
        Should.Throw<DomainException>(() => Invoice.Create(
            "acme", Fixture.OrderId, Guid.NewGuid(), "sup-1", "INV-0", new DateOnly(2026, 10, 27), 0, InvoiceSource.Manual,
            [new InvoiceLine(1, "LAP-14", "Laptop", 1, Money.Zar(1m))], Money.Zar(1.15m), "{}", Fixture.Now));
}
