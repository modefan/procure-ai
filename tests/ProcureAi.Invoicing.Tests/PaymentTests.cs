using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Domain;
using Shouldly;

namespace ProcureAi.Invoicing.Tests;

public sealed class PaymentTests
{
    [Fact]
    public void Payable_quantity_is_what_is_received_even_when_the_invoice_is_for_more()
    {
        var order = Fixture.Order(received: 2);
        var invoice = Fixture.Matched(Fixture.Invoice(quantity: 3), order);

        var plan = invoice.PlanPayment(order, null);

        plan.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(2);
        plan.TotalExclVat.ShouldBe(Money.Zar(50_400m));
        plan.Vat.ShouldBe(Money.Zar(7_560m));
        plan.TotalInclVat.ShouldBe(Money.Zar(57_960m));
        plan.TotalInclVat.ToDisplayString().ShouldBe("R 57 960,00");
    }

    [Fact]
    public void A_payment_is_made_at_the_order_price_not_the_invoice_price()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(quantity: 2, price: 25_200.25m), order);

        invoice.Status.ShouldBe(InvoiceStatus.Matched);
        invoice.PlanPayment(order, null).TotalExclVat.ShouldBe(Money.Zar(50_400m));
    }

    [Fact]
    public void More_than_payable_is_refused()
    {
        var order = Fixture.Order(received: 2);
        var invoice = Fixture.Matched(Fixture.Invoice(quantity: 3), order);

        var error = Should.Throw<DomainException>(() => invoice.PlanPayment(order, [(1, 3)]));

        error.Message.ShouldBe("Line 1: 3 requested, but only 2 can be paid.");
    }

    [Fact]
    public void A_chosen_quantity_below_payable_pays_just_that()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(), order);

        var plan = invoice.PlanPayment(order, [(1, 1)]);

        plan.TotalExclVat.ShouldBe(Money.Zar(25_200m));
    }

    [Fact]
    public void An_invoice_on_hold_cannot_be_paid()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(), order);
        invoice.Hold("Supplier bank details changed");

        var error = Should.Throw<DomainException>(() => invoice.PlanPayment(order, null));

        error.Message.ShouldBe("The invoice is on hold: Supplier bank details changed");
        invoice.ReleaseHold();
        invoice.PlanPayment(order, null).Lines.ShouldNotBeEmpty();
    }

    [Fact]
    public void After_paying_what_was_received_the_next_delivery_makes_the_rest_payable()
    {
        var order = Fixture.Order(received: 2);
        var invoice = Fixture.Matched(Fixture.Invoice(quantity: 3), order);
        invoice.Pay(invoice.PlanPayment(order, null), PaymentNumber.FromSequence(1), "fiona", Fixture.Now);
        invoice.Status.ShouldBe(InvoiceStatus.PartiallyPaid);
        Should.Throw<DomainException>(() => invoice.PlanPayment(order, null)).Message.ShouldBe("Nothing can be paid on this invoice yet.");

        order.ApplyReceipt([(1, 1)]);
        var plan = invoice.PlanPayment(order, null);

        plan.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(1);
        invoice.Pay(plan, PaymentNumber.FromSequence(2), "fiona", Fixture.Now);
        invoice.Status.ShouldBe(InvoiceStatus.Paid);
    }

    [Fact]
    public void A_line_whose_price_failed_is_never_payable()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(price: 25_300m), order);
        invoice.Status.ShouldBe(InvoiceStatus.Mismatched);

        Should.Throw<DomainException>(() => invoice.PlanPayment(order, null)).Message
            .ShouldBe("Nothing can be paid on this invoice yet.");
        Should.Throw<DomainException>(() => invoice.PlanPayment(order, [(1, 1)])).Message
            .ShouldBe("Line 1: 1 requested, but only 0 can be paid.");
    }

    [Fact]
    public void An_invoice_that_was_not_matched_cannot_be_paid()
    {
        var order = Fixture.Order(received: 3);

        Should.Throw<DomainException>(() => Fixture.Invoice().PlanPayment(order, null)).Message
            .ShouldBe("The invoice has not been matched against an order yet.");
    }

    [Fact]
    public void A_cancelled_order_cannot_be_paid()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(), order);
        order.Cancel();

        Should.Throw<DomainException>(() => invoice.PlanPayment(order, null)).Message.ShouldBe("PO-1001 was cancelled.");
    }

    [Fact]
    public void A_rejected_order_cannot_be_paid()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(), order);
        order.Reject();

        Should.Throw<DomainException>(() => invoice.PlanPayment(order, null)).Message.ShouldBe("PO-1001 was rejected by the supplier.");
    }

    [Fact]
    public void Requested_lines_must_exist_be_positive_and_unique()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(), order);

        Should.Throw<DomainException>(() => invoice.PlanPayment(order, [(2, 1)])).Message.ShouldBe("The invoice has no line 2.");
        Should.Throw<DomainException>(() => invoice.PlanPayment(order, [(1, 0)])).Message.ShouldBe("Line 1: the quantity to pay must be positive.");
        Should.Throw<DomainException>(() => invoice.PlanPayment(order, [(1, 1), (1, 1)])).Message.ShouldBe("Line 1 appears more than once.");
    }

    [Fact]
    public void Paying_updates_the_invoice_and_the_order_view_together()
    {
        var order = Fixture.Order(received: 2);
        var invoice = Fixture.Matched(Fixture.Invoice(quantity: 3), order);
        var revision = order.Revision;

        var payment = invoice.Pay(invoice.PlanPayment(order, null), PaymentNumber.FromSequence(1), "fiona", Fixture.Now);

        payment.Number.ToString().ShouldBe("PAY-0001");
        payment.ReleasedBy.ShouldBe("fiona");
        payment.TotalInclVat.ShouldBe(Money.Zar(57_960m));
        payment.ExportStatus.ShouldBe(ExportStatus.Pending);
        invoice.Lines[0].Paid.ShouldBe(2);
        order.Lines[0].Paid.ShouldBe(2);
        order.PaidInclVat.ShouldBe(Money.Zar(57_960m));
        order.Revision.ShouldBeGreaterThan(revision);
        invoice.Payments.ShouldHaveSingleItem();
    }

    [Fact]
    public void Two_invoices_cannot_both_be_paid_for_the_same_received_goods()
    {
        var order = Fixture.Order(received: 3);
        var first = Fixture.Matched(Fixture.Invoice(quantity: 3, sequence: 1, number: "INV-1"), order);
        var second = Fixture.Invoice(quantity: 3, sequence: 2, number: "INV-2");
        // Matched by hand as if it passed: the order view still caps the payment at received minus paid.
        second.ApplyMatch(new MatchResult(true, [new LineMatch(1, true, true, 3, 3, 0, null)], []));
        var stalePlan = second.PlanPayment(order, null);
        first.Pay(first.PlanPayment(order, null), PaymentNumber.FromSequence(1), "fiona", Fixture.Now);

        Should.Throw<DomainException>(() => second.Pay(stalePlan, PaymentNumber.FromSequence(2), "fiona", Fixture.Now));
    }

    [Fact]
    public void A_plan_made_for_another_invoice_is_refused()
    {
        var order = Fixture.Order(received: 3);
        var one = Fixture.Matched(Fixture.Invoice(number: "INV-1"), order);
        var other = Fixture.Matched(Fixture.Invoice(number: "INV-2", sequence: 2), order);

        Should.Throw<DomainException>(() => other.Pay(one.PlanPayment(order, null), PaymentNumber.FromSequence(1), "fiona", Fixture.Now))
            .Message.ShouldBe("That payment plan is for another invoice.");
    }

    [Fact]
    public void Export_is_recorded_once()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(), order);
        var payment = invoice.Pay(invoice.PlanPayment(order, null), PaymentNumber.FromSequence(1), "fiona", Fixture.Now);

        payment.RecordExportFailure("ERP timed out");
        payment.ExportAttempts.ShouldBe(1);
        payment.LastExportError.ShouldBe("ERP timed out");
        payment.MarkExported("ERP-77").ShouldBeTrue();
        payment.MarkExported("ERP-77").ShouldBeFalse();
        payment.ExportStatus.ShouldBe(ExportStatus.Exported);
        payment.ErpReference.ShouldBe("ERP-77");
        payment.LastExportError.ShouldBeNull();
        Should.Throw<DomainException>(() => payment.RecordExportFailure("late"));
    }

    [Fact]
    public void PaymentNumber_is_padded_to_four_digits()
    {
        PaymentNumber.FromSequence(12).ToString().ShouldBe("PAY-0012");
        Should.Throw<DomainException>(() => PaymentNumber.FromSequence(0));
    }

    [Fact]
    public void An_invoice_from_another_supplier_is_never_payable_even_when_prices_match()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(supplier: "sup-2"), order);
        invoice.Match!.Lines.ShouldAllBe(l => l.PriceOk);

        Should.Throw<DomainException>(() => invoice.PlanPayment(order, null)).Message
            .ShouldBe("Invoice is from sup-2, the order is with sup-1");
        Should.Throw<DomainException>(() => invoice.PlanPayment(order, [(1, 1)])).Message
            .ShouldBe("Invoice is from sup-2, the order is with sup-1");
    }

    [Fact]
    public void The_erp_vendor_is_the_supplier_of_the_order()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Matched(Fixture.Invoice(), order);
        var payment = invoice.Pay(invoice.PlanPayment(order, null), PaymentNumber.FromSequence(1), "fiona", Fixture.Now);
        var other = Fixture.Order(received: 3, supplier: "sup-9");

        ProcureAi.Invoicing.Application.Commands.ErpPayments.Build(invoice, payment, order).SupplierId.ShouldBe("sup-1");
        ProcureAi.Invoicing.Application.Commands.ErpPayments.Build(invoice, payment, other).SupplierId.ShouldBe("sup-9");
    }

    [Fact]
    public void Every_state_changing_invoice_operation_bumps_the_revision()
    {
        var order = Fixture.Order(received: 3);
        var invoice = Fixture.Invoice(quantity: 2);
        var revision = invoice.Revision;

        invoice.AttachOrder(order);
        invoice.Revision.ShouldBe(revision + 1);
        invoice.ApplyMatch(ThreeWayMatch.Evaluate(invoice, order, [], Fixture.Tolerance));
        invoice.Revision.ShouldBe(revision + 2);
        invoice.Hold("check");
        invoice.Revision.ShouldBe(revision + 3);
        invoice.ReleaseHold();
        invoice.Revision.ShouldBe(revision + 4);
        invoice.Pay(invoice.PlanPayment(order, [(1, 1)]), PaymentNumber.FromSequence(1), "fiona", Fixture.Now);
        invoice.Revision.ShouldBe(revision + 5);
        invoice.Status.ShouldBe(InvoiceStatus.PartiallyPaid);
        invoice.Pay(invoice.PlanPayment(order, [(1, 1)]), PaymentNumber.FromSequence(2), "fiona", Fixture.Now);
        invoice.Revision.ShouldBe(revision + 6);
    }
}
