using ProcureAi.Assistant.Application.Retrieval;
using Shouldly;

namespace ProcureAi.Assistant.Tests.Retrieval;

public sealed class RrfMergerTests
{
    [Fact]
    public void Rrf_ranks_item_high_in_both_arms_first()
    {
        // "b" is second in both arms; "a" is first in one arm but only fourth in the other; "x" is first in one arm only.
        string[] vector = ["a", "b", "c", "d"];
        string[] text = ["x", "b", "y", "a"];

        var merged = RrfMerger.Merge([vector, text], k: 60);

        merged[0].Item.ShouldBe("b");
        merged[0].Score.ShouldBe(2.0 / 62, 1e-12);
        // "a": 1/61 + 1/64 < 2/62, but beats "x": 1/61 alone; "c" and "y" tie at 1/63 (the first arm's first); "d": 1/64.
        merged.Select(r => r.Item).ShouldBe(["b", "a", "x", "c", "y", "d"]);
        merged.Select(r => r.Score).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public void Rrf_handles_item_in_one_arm_only()
    {
        string[] vector = ["a", "b"];
        string[] text = [];

        var merged = RrfMerger.Merge([vector, text], k: 60);

        merged.Select(r => r.Item).ShouldBe(["a", "b"]);
        merged[0].Score.ShouldBe(1.0 / 61, 1e-12);
        merged[1].Score.ShouldBe(1.0 / 62, 1e-12);
    }

    [Fact]
    public void Rrf_ties_keep_the_earlier_arm_first()
    {
        // "a" and "x" both score 1/61: the first arm's item comes first, deterministically.
        var merged = RrfMerger.Merge([["a"], ["x"]], k: 60);

        merged.Select(r => r.Item).ShouldBe(["a", "x"]);
    }

    [Fact]
    public void Rrf_counts_an_item_repeated_in_one_arm_once()
    {
        var merged = RrfMerger.Merge([["a", "a", "b"]], k: 60);

        merged.Select(r => r.Item).ShouldBe(["a", "b"]);
        merged[0].Score.ShouldBe(1.0 / 61, 1e-12);
        merged[1].Score.ShouldBe(1.0 / 63, 1e-12);
    }

    [Fact]
    public void Rrf_needs_a_positive_k() =>
        Should.Throw<ArgumentOutOfRangeException>(() => RrfMerger.Merge([["a"]], k: 0));
}
