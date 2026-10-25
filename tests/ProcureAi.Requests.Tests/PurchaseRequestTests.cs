using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Requests.Domain;
using Shouldly;

namespace ProcureAi.Requests.Tests;

public class PurchaseRequestTests
{
    private static PurchaseRequest NewDraft(string title = "Laptops") =>
        PurchaseRequest.CreateDraft("acme", RequestNumber.From(1), "user-1", title, "CC-100", "sup-1");

    private static PurchaseRequest DraftWithLines()
    {
        var request = NewDraft();
        request.AddLine("LAP-1", "Laptop", 3, Money.Zar(25200m));
        return request;
    }

    [Fact]
    public void Totals_include_vat()
    {
        var request = DraftWithLines();

        request.TotalExclVat.Amount.ShouldBe(75600m);
        request.Vat.Amount.ShouldBe(11340m);
        request.TotalInclVat.Amount.ShouldBe(86940m);
    }

    [Fact]
    public void Lines_are_numbered_from_one_and_not_renumbered_on_remove()
    {
        var request = NewDraft();
        request.AddLine("A", "a", 1, Money.Zar(1m));
        request.AddLine("B", "b", 1, Money.Zar(2m));
        request.AddLine("C", "c", 1, Money.Zar(3m));

        request.RemoveLine(2);

        request.Lines.Select(l => l.LineNo).ShouldBe([1, 3]);
        Should.Throw<DomainException>(() => request.RemoveLine(2));
    }

    [Fact]
    public void Submit_raises_PurchaseRequestSubmitted_with_total_incl_vat()
    {
        var request = DraftWithLines();
        var now = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);

        request.Submit(now);

        request.Status.ShouldBe(RequestStatus.Submitted);
        request.SubmittedAt.ShouldBe(now);
        var evt = request.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PurchaseRequestSubmitted>();
        evt.RequestId.ShouldBe(request.Id);
        evt.TenantId.ShouldBe("acme");
        evt.Number.ShouldBe("PR-0001");
        evt.Title.ShouldBe(request.Title);
        evt.RequesterId.ShouldBe("user-1");
        evt.CostCenterCode.ShouldBe("CC-100");
        evt.TotalInclVat.ShouldBe(86940m);
        evt.SubmittedAt.ShouldBe(now);
    }

    [Fact]
    public void Cannot_submit_without_lines() =>
        Should.Throw<DomainException>(() => NewDraft().Submit(DateTimeOffset.UtcNow));

    [Fact]
    public void Cannot_change_after_submit()
    {
        var request = DraftWithLines();
        request.Submit(DateTimeOffset.UtcNow);

        Should.Throw<DomainException>(() => request.AddLine("X", "x", 1, Money.Zar(1m)));
        Should.Throw<DomainException>(() => request.UpdateTitle("New"));
        Should.Throw<DomainException>(() => request.RemoveLine(1));
    }

    [Fact]
    public void Cannot_submit_twice()
    {
        var request = DraftWithLines();
        request.Submit(DateTimeOffset.UtcNow);

        Should.Throw<DomainException>(() => request.Submit(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CreateDraft_rejects_default_request_number() =>
        Should.Throw<DomainException>(() =>
            PurchaseRequest.CreateDraft("acme", default, "user-1", "Laptops", "CC-100", "sup-1"));

    [Fact]
    public void AddLine_rejects_non_zar_money() =>
        Should.Throw<DomainException>(() => NewDraft().AddLine("A", "a", 1, new Money(5m, "USD")));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Quantity_must_be_positive(int quantity) =>
        Should.Throw<DomainException>(() => NewDraft().AddLine("A", "a", quantity, Money.Zar(1m)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_is_required(string title)
    {
        Should.Throw<DomainException>(() => NewDraft(title));
        Should.Throw<DomainException>(() => NewDraft().UpdateTitle(title));
    }

    [Fact]
    public void Update_title_changes_title() =>
        NewDraft().Also(r => { r.UpdateTitle("Monitors"); r.Title.ShouldBe("Monitors"); });

    [Theory]
    [InlineData(1, "PR-0001")]
    [InlineData(42, "PR-0042")]
    [InlineData(12345, "PR-12345")]
    public void RequestNumber_formats_and_parses(int sequence, string text)
    {
        RequestNumber.From(sequence).ToString().ShouldBe(text);
        RequestNumber.Parse(text).Sequence.ShouldBe(sequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RequestNumber_sequence_must_be_positive(int sequence) =>
        Should.Throw<DomainException>(() => RequestNumber.From(sequence));

    [Theory]
    [InlineData("")]
    [InlineData("PR-")]
    [InlineData("PR-abc")]
    [InlineData("XX-0001")]
    [InlineData("PR-0")]
    public void RequestNumber_rejects_bad_text(string text) =>
        Should.Throw<DomainException>(() => RequestNumber.Parse(text));
}

internal static class TestExtensions
{
    public static void Also<T>(this T value, Action<T> action) => action(value);
}
