using Shouldly;

namespace ProcureAi.DemoData.Tests;

/// <summary>The rules a scenario must follow before the seeder sends anything (<see cref="Scenarios.Validate"/>).</summary>
public sealed class ScenarioRuleTests
{
    private static ScenarioRequest Request(StopAt stopAt, string supplier = "contoso-hardware") =>
        new("a-key", "A title", "tom@acme.test", "IT-200", supplier, [new ScenarioLine("MON-27", 2)], stopAt, Approvers: ["maria@acme.test"]);

    private static IReadOnlyList<string> Errors(ScenarioRequest request) =>
        Scenarios.Validate(new Scenario("acme", "thandi@acme.test", [request]));

    [Fact]
    public void Shipped_scenarios_follow_the_rules()
    {
        foreach (var scenario in DemoWorld.Scenarios)
        {
            Scenarios.Validate(scenario).ShouldBeEmpty();
        }
    }

    [Fact]
    public void A_paper_invoice_is_only_for_a_supplier_that_sends_none()
    {
        Errors(Request(StopAt.Invoiced) with { Invoice = new ScenarioInvoice() }).ShouldContain(e => e.Contains("invoices by itself"));
        Errors(Request(StopAt.Invoiced, "fabrikam-office")).ShouldContain(e => e.Contains("needs a paper invoice"));
        Errors(Request(StopAt.Invoiced, "fabrikam-office") with { Invoice = new ScenarioInvoice() }).ShouldBeEmpty();
    }

    [Fact]
    public void Mismatched_needs_a_price_difference_and_nothing_else_has_one()
    {
        Errors(Request(StopAt.Mismatched, "fabrikam-office") with { Invoice = new ScenarioInvoice() }).ShouldNotBeEmpty();
        Errors(Request(StopAt.Mismatched, "fabrikam-office") with { Invoice = new ScenarioInvoice(250m) }).ShouldBeEmpty();
        Errors(Request(StopAt.Invoiced, "fabrikam-office") with { Invoice = new ScenarioInvoice(250m) }).ShouldNotBeEmpty();
    }

    [Fact]
    public void Payment_follows_the_stop_and_never_by_the_requester()
    {
        Errors(Request(StopAt.Completed)).ShouldContain(e => e.Contains("needs pay: Full"));
        Errors(Request(StopAt.PartiallyPaid) with { Pay = PayMode.Partial }).ShouldBeEmpty();
        Errors(Request(StopAt.PartiallyPaid) with { Pay = PayMode.Partial, Lines = [new ScenarioLine("MON-27", 1)] })
            .ShouldContain(e => e.Contains("2 or more"));
        Errors(Request(StopAt.Received) with { Pay = PayMode.Full }).ShouldContain(e => e.Contains("is not paid"));
        Scenarios.Validate(new Scenario("acme", "tom@acme.test", [Request(StopAt.Completed) with { Pay = PayMode.Full }]))
            .ShouldContain(e => e.Contains("separation of duties"));
    }

    [Fact]
    public void Partial_receipts_belong_to_partially_received_and_closed_short()
    {
        var part = new[] { new ScenarioLine("MON-27", 1) };
        Errors(Request(StopAt.PartiallyReceived)).ShouldContain(e => e.Contains("needs the part"));
        Errors(Request(StopAt.PartiallyReceived) with { Receive = part }).ShouldBeEmpty();
        Errors(Request(StopAt.PartiallyReceived) with { Receive = [new ScenarioLine("MON-27", 2)] }).ShouldContain(e => e.Contains("not all of them"));
        Errors(Request(StopAt.Received) with { Receive = part }).ShouldNotBeEmpty();
        Errors(Request(StopAt.ClosedShort) with { Receive = part, Pay = PayMode.Full }).ShouldContain(e => e.Contains("close-short reason"));
        Errors(Request(StopAt.ClosedShort) with { Receive = part, Pay = PayMode.Full, CloseShortReason = "The rest is discontinued" }).ShouldBeEmpty();
    }

    [Fact]
    public void Keys_and_titles_are_unique_and_approvals_are_named()
    {
        var request = Request(StopAt.Submitted);
        Scenarios.Validate(new Scenario("acme", "thandi@acme.test", [request, request with { Title = "Other" }])).ShouldContain(e => e.Contains("more than once"));
        Scenarios.Validate(new Scenario("acme", "thandi@acme.test", [request, request with { Key = "b-key" }])).ShouldContain(e => e.Contains("two requests titled"));
        Errors(Request(StopAt.Ordered) with { Approvers = null }).ShouldContain(e => e.Contains("needs the approvers"));
        Errors(Request(StopAt.Draft)).ShouldContain(e => e.Contains("never approved"));
        Errors(request with { Key = "Not A Key" }).ShouldContain(e => e.Contains("lowercase"));
    }
}
