using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Assistant.Tests.Retrieval;

public sealed class PurchaseChunkTextTests
{
    private static Purchase Completed()
    {
        var purchase = new Purchase(
            Guid.NewGuid(), "acme", "PR-0982", "Laptops for IT", "IT-200", Money.Zar(140_000m), "SUP-1", "2 x Laptop 14 inch, 1 x Docking station",
            DateTimeOffset.Parse("2026-09-01T08:00:00+02:00"));
        purchase.RecordApproved(["maria", "sipho"], DateTimeOffset.Parse("2026-09-02T08:00:00+02:00"));
        purchase.RecordCompleted(DateTimeOffset.Parse("2026-09-14T08:00:00+02:00"));
        return purchase;
    }

    [Fact]
    public void Text_follows_ruling_9_with_en_ZA_money()
    {
        var text = Completed().ToChunkText(["Maria Naidoo", "Sipho Dlamini"], new DateOnly(2026, 9, 14));

        text.ShouldBe(
            "PR-0982 · Laptops for IT · IT-200 · R 140 000,00 incl. VAT · approved by Maria Naidoo, Sipho Dlamini · completed 2026-09-14"
            + " · lines: 2 x Laptop 14 inch, 1 x Docking station");
    }

    [Fact]
    public void Text_leaves_out_the_approval_when_no_approver_is_known() =>
        Completed().ToChunkText([], new DateOnly(2026, 9, 14)).ShouldNotContain("approved by");

    [Fact]
    public void A_placeholder_or_unfinished_purchase_is_not_indexable()
    {
        var placeholder = Purchase.Placeholder(Guid.NewGuid(), "acme", "PR-1");
        placeholder.RecordCompleted(DateTimeOffset.UtcNow);
        placeholder.HasSubmitData.ShouldBeFalse();
        placeholder.IsIndexable.ShouldBeFalse();
        Should.Throw<DomainException>(() => placeholder.ToChunkText([], new DateOnly(2026, 9, 14)));

        var approved = new Purchase(Guid.NewGuid(), "acme", "PR-2", "Chairs", "OPS-1", Money.Zar(10m), null, "1 x Chair", DateTimeOffset.UtcNow);
        approved.RecordApproved(["maria"], DateTimeOffset.UtcNow);
        approved.Status.ShouldBe(PurchaseStatus.Approved);
        approved.IsIndexable.ShouldBeFalse();
    }

    [Fact]
    public void Events_in_any_order_end_completed_with_all_data()
    {
        var purchase = Purchase.Placeholder(Guid.NewGuid(), "acme", "PR-3");
        purchase.RecordCompleted(DateTimeOffset.UtcNow);
        purchase.RecordApproved(["thandi", "thandi", " "], DateTimeOffset.UtcNow);
        purchase.RecordSubmitted("PR-3", "Desks", "OPS-1", Money.Zar(5_000m), null, "2 x Desk", DateTimeOffset.UtcNow);

        purchase.Status.ShouldBe(PurchaseStatus.Completed);
        purchase.IsIndexable.ShouldBeTrue();
        purchase.ApproverIdList.ShouldBe(["thandi"]);
    }
}
