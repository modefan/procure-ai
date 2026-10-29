using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Ingestion;
using Shouldly;
using UglyToad.PdfPig.Writer;

namespace ProcureAi.Assistant.Tests.Ingestion;

/// <summary>Text extraction (Markdown, PDF) and structure-aware chunking: pure, no containers.</summary>
public sealed class ChunkingTests
{
    private static readonly string[] ApprovalThresholdsPath = ["Procurement Policy", "4 Approvals", "4.2 Approval thresholds"];

    private static string FilePath(string name) => Path.Combine(AppContext.BaseDirectory, "Files", name);

    private static ExtractedDocument ExtractMarkdown()
    {
        using var stream = File.OpenRead(FilePath("policy.md"));
        return new MarkdownTextExtractor().Extract(stream);
    }

    private static ExtractedDocument ExtractPdf()
    {
        using var stream = File.OpenRead(FilePath("policy.pdf"));
        return new PdfPigTextExtractor().Extract(stream);
    }

    private static ChunkPlanner Planner() => new(new ChunkingOptions { MinTokens = 300, MaxTokens = 800, OverlapPercent = 12 });

    private static int Tokens(string text) => (int)Math.Ceiling(text.Length / 4.0);

    [Fact]
    public void Markdown_sections_keep_heading_path()
    {
        var document = ExtractMarkdown();

        var thresholds = document.Sections.Where(s => s.Path.SequenceEqual(ApprovalThresholdsPath)).ToList();
        thresholds.Count.ShouldBe(2, "the sentence above the table and the table itself");
        thresholds.Single(s => s.IsTable).Text.ShouldContain("Finance director");
        thresholds.Single(s => !s.IsTable).Text.ShouldStartWith("The approval level is decided");
        document.Sections.ShouldContain(s => s.Path.SequenceEqual(new[] { "Procurement Policy" }));
        document.Sections.ShouldContain(s => s.Path.SequenceEqual(new[] { "Procurement Policy", "4 Approvals" }));
    }

    [Fact]
    public void Pdf_numbered_headings_become_sections()
    {
        var document = ExtractPdf();

        document.Sections.ShouldNotBeEmpty();
        var paths = document.Sections.Select(s => string.Join(" > ", s.Path)).Distinct().ToList();
        paths.ShouldContain("Procurement Policy > 4 Approvals > 4.2 Approval thresholds");
        paths.ShouldContain("Procurement Policy > 4 Approvals > 4.3 Escalation and timeouts");
        paths.ShouldContain("Procurement Policy > 6 Invoices and payment");
        var table = document.Sections.Single(s => s.IsTable);
        table.Path.ShouldBe(ApprovalThresholdsPath);
        table.Text.Split('\n').Length.ShouldBe(5, "the header row and four data rows");
        table.Text.ShouldContain("Executive committee");
        document.Sections.Single(s => s.Path[^1] == "6 Invoices and payment").Text.ShouldContain("three-way match");
        document.Sections.Any(s => s.Text.Contains("4.2 Approval thresholds")).ShouldBeFalse("headings are paths, not body text");
    }

    [Fact]
    public void Long_section_splits_with_overlap_within_bounds()
    {
        foreach (var document in new[] { ExtractMarkdown(), ExtractPdf() })
        {
            var chunks = Planner().Plan(document);
            string[] path = ["Procurement Policy", "4 Approvals", "4.3 Escalation and timeouts"];
            var children = chunks.Where(c => c.Kind == ChunkKind.Child && c.SectionPath.SequenceEqual(path)).ToList();

            children.Count.ShouldBeGreaterThan(1);
            var parent = chunks.Single(c => c.Kind == ChunkKind.Parent && c.SectionPath.SequenceEqual(path));
            children.ShouldAllBe(c => c.ParentOrdinal == parent.Ordinal);
            children.ShouldAllBe(c => Tokens(c.Text) <= 800);
            children.ShouldAllBe(c => Tokens(c.Text) >= 300);
            for (var i = 1; i < children.Count; i++)
            {
                var overlap = LongestSuffixPrefix(children[i - 1].Text, children[i].Text);
                overlap.ShouldBeGreaterThan(150, "about 12 % of 800 tokens is shared with the previous chunk");
                overlap.ShouldBeLessThanOrEqualTo((3200 * 12 / 100) + 20);
            }

            // Nothing is lost: joining the children without their overlaps gives the section text back.
            var rebuilt = children[0].Text;
            for (var i = 1; i < children.Count; i++)
            {
                rebuilt += children[i].Text[LongestSuffixPrefix(children[i - 1].Text, children[i].Text)..];
            }

            rebuilt.ShouldBe(parent.Text);
        }
    }

    [Fact]
    public void Long_text_without_paragraphs_or_sentences_splits_on_words()
    {
        var text = string.Join(' ', Enumerable.Range(0, 2500).Select(i => $"word{i}"));
        var document = new ExtractedDocument([new Section(["Doc"], text, false)]);

        var children = Planner().Plan(document).Where(c => c.Kind == ChunkKind.Child).ToList();

        children.Count.ShouldBeGreaterThan(3);
        children.ShouldAllBe(c => Tokens(c.Text) <= 800);
        children.ShouldAllBe(c => Tokens(c.Text) >= 300);
        // Every child is a run of whole words, consecutive in the input; together they reach both ends.
        var firstIndexes = new List<int>();
        foreach (var child in children)
        {
            var words = child.Text.Split(' ');
            var first = int.Parse(words[0]["word".Length..]);
            words.ShouldBe(Enumerable.Range(first, words.Length).Select(i => $"word{i}"));
            firstIndexes.Add(first);
        }

        firstIndexes[0].ShouldBe(0);
        children[^1].Text.ShouldEndWith("word2499");
    }

    [Fact]
    public void A_giant_word_still_splits_and_terminates()
    {
        var document = new ExtractedDocument([new Section(["Doc"], new string('x', 20_000), false)]);

        var children = Planner().Plan(document).Where(c => c.Kind == ChunkKind.Child).ToList();

        children.Count.ShouldBeGreaterThan(5);
        children.ShouldAllBe(c => Tokens(c.Text) <= 800);
    }

    [Fact]
    public void Table_is_one_chunk()
    {
        var table = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"| Item {i} | R {i * 10},00 | Supplier number {i} |"));
        Tokens(table).ShouldBeGreaterThan(800);
        var document = new ExtractedDocument(
        [
            new Section(["Doc", "1 Prices"], "Intro text.", false),
            new Section(["Doc", "1 Prices"], table, true),
        ]);

        var children = Planner().Plan(document).Where(c => c.Kind == ChunkKind.Child).ToList();

        children.Count.ShouldBe(2);
        var tableChunk = children.Single(c => c.Text.Contains("Item 399"));
        tableChunk.Text.ShouldContain("Item 0 ");

        var policy = Planner().Plan(ExtractMarkdown()).Where(c => c.Kind == ChunkKind.Child && c.Text.Contains("Executive committee")).ToList();
        policy.Count.ShouldBe(1);
        policy[0].Text.ShouldContain("Line manager");
    }

    [Fact]
    public void Short_section_is_one_child_under_its_parent()
    {
        var chunks = Planner().Plan(ExtractMarkdown());

        string[] path = ["Procurement Policy", "5 Suppliers"];
        var parent = chunks.Single(c => c.Kind == ChunkKind.Parent && c.SectionPath.SequenceEqual(path));
        var child = chunks.Single(c => c.Kind == ChunkKind.Child && c.SectionPath.SequenceEqual(path));
        parent.ParentOrdinal.ShouldBeNull();
        child.ParentOrdinal.ShouldBe(parent.Ordinal);
        child.Text.ShouldBe(parent.Text);
        child.Text.ShouldContain("tax clearance certificate");
        chunks.Select(c => c.Ordinal).ShouldBe(Enumerable.Range(0, chunks.Count), "ordinals are 0, 1, 2 ... in order");
        chunks.Where(c => c.Kind == ChunkKind.Parent).ShouldAllBe(c => c.ParentOrdinal == null);
    }

    [Fact]
    public void Same_text_gives_same_hash_changed_text_changes_it()
    {
        static ExtractedDocument Doc(string text, string heading = "1 A") => new([new Section(["Doc", heading], text, false)]);

        var first = Planner().Plan(Doc("Orders need approval."));
        var again = Planner().Plan(Doc("  Orders   need\tapproval. \r\n\r\n\r\n"));
        var changed = Planner().Plan(Doc("Orders need two approvals."));
        var moved = Planner().Plan(Doc("Orders need approval.", "2 B"));

        first[1].ContentHash.ShouldBe(again[1].ContentHash, "whitespace differences do not change the hash");
        first[1].ContentHash.ShouldMatch("^[0-9a-f]{64}$");
        first[1].ContentHash.ShouldNotBe(changed[1].ContentHash);
        first[1].ContentHash.ShouldNotBe(moved[1].ContentHash, "the section path is part of the hash");
        first[1].ContentHash.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Doc > 1 A\nOrders need approval."))));
    }

    [Fact]
    public void Empty_or_image_only_pdf_gives_no_sections()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(595, 842);
        using var blank = new MemoryStream(builder.Build());

        new PdfPigTextExtractor().Extract(blank).Sections.ShouldBeEmpty();
        Planner().Plan(new ExtractedDocument([])).ShouldBeEmpty();
        new MarkdownTextExtractor().Extract(new MemoryStream(Encoding.UTF8.GetBytes("\n\n  \n"))).Sections.ShouldBeEmpty();
    }

    [Fact]
    public void Extractors_are_chosen_by_content_type()
    {
        new PdfPigTextExtractor().Supports("application/pdf").ShouldBeTrue();
        new PdfPigTextExtractor().Supports("text/markdown").ShouldBeFalse();
        new MarkdownTextExtractor().Supports("text/markdown").ShouldBeTrue();
        new MarkdownTextExtractor().Supports("text/markdown; charset=utf-8").ShouldBeTrue();
    }

    [Fact]
    public void Parent_cap_below_child_size_fails_validation()
    {
        var options = new ChunkingOptions { MaxTokens = 800, MaxParentTokens = 500 };

        Validator.TryValidateObject(options, new ValidationContext(options), [], true).ShouldBeFalse();
        var defaults = new ChunkingOptions();
        Validator.TryValidateObject(defaults, new ValidationContext(defaults), [], true).ShouldBeTrue();
    }

    [Fact]
    public void Invalid_chunking_options_fail_validation()
    {
        var options = new ChunkingOptions { MinTokens = 500, MaxTokens = 600 };

        Validator.TryValidateObject(options, new ValidationContext(options), [], true).ShouldBeFalse();
    }

    [Fact]
    public void Pdf_table_keeps_its_cells_through_planning()
    {
        var chunks = Planner().Plan(ExtractPdf()).Where(c => c.Kind == ChunkKind.Child && c.Text.Contains("Executive committee")).ToList();

        chunks.Count.ShouldBe(1);
        var rows = chunks[0].Text.Split('\n');
        rows.Length.ShouldBe(5);
        rows.ShouldAllBe(r => r.Split(" | ").Length == 3, "three cells per row");
        rows.ShouldContain("Up to R 20 000,00 | Line manager | 2 working days");
        rows[0].ShouldBe("Amount (ZAR) | Approver | Maximum time to decide");
    }

    [Fact]
    public void Undetected_headings_give_bounded_parents()
    {
        var paragraph = string.Join(' ', Enumerable.Range(0, 90).Select(i => $"Sentence number {i} says something."));
        var text = string.Join("\n\n", Enumerable.Repeat(paragraph, 12));
        var document = new ExtractedDocument([new Section([], text, false)]);

        var chunks = Planner().Plan(document);

        var parents = chunks.Where(c => c.Kind == ChunkKind.Parent).ToList();
        parents.Count.ShouldBeGreaterThan(1);
        parents.ShouldAllBe(p => Tokens(p.Text) <= 2000);
        foreach (var child in chunks.Where(c => c.Kind == ChunkKind.Child))
        {
            var parent = parents.Single(p => p.Ordinal == child.ParentOrdinal);
            parent.Text.ShouldContain(child.Text);
        }
    }

    [Fact]
    public void Parent_and_child_of_short_section_differ_by_kind_not_hash()
    {
        var chunks = Planner().Plan(new ExtractedDocument([new Section(["Doc", "1 A"], "Orders need approval.", false)]));

        chunks.Count.ShouldBe(2);
        chunks[0].ContentHash.ShouldBe(chunks[1].ContentHash, "same path and text");
        chunks.Select(c => (c.Kind, c.ContentHash)).Distinct().Count().ShouldBe(2, "reuse is keyed by (kind, hash)");
    }

    [Fact]
    public void Non_breaking_and_other_unicode_spaces_are_normalised_like_spaces()
    {
        static ExtractedDocument Doc(string text) => new([new Section(["Doc"], text, false)]);

        var plain = Planner().Plan(Doc("Orders need approval above R 5 000,00."));
        var odd = Planner().Plan(Doc("Orders\u00A0need\u2009approval  above\u3000R\u00A05\u202F000,00."));

        odd[1].Text.ShouldBe(plain[1].Text);
        odd[1].ContentHash.ShouldBe(plain[1].ContentHash);
    }

    [Fact]
    public void Markdown_closing_hashes_are_stripped_only_after_a_space()
    {
        const string markdown = "## Learning C#\nText one.\n\n## Closed ##\nText two.\n\n### Tagged C# ###\nText three.\n";

        var sections = new MarkdownTextExtractor().Extract(new MemoryStream(Encoding.UTF8.GetBytes(markdown))).Sections;

        sections.Select(s => s.Path[^1]).ShouldBe(["Learning C#", "Closed", "Tagged C#"]);
    }

    [Fact]
    public void Markdown_heading_with_only_hashes_is_empty_and_long_space_runs_are_fast()
    {
        var markdown = "# Doc\n## #\nBody under an empty heading.\n\n## " + "x" + new string(' ', 100_000) + "y" + "\nTail.\n";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var sections = new MarkdownTextExtractor().Extract(new MemoryStream(Encoding.UTF8.GetBytes(markdown))).Sections;
        var planned = Planner().Plan(new ExtractedDocument([new Section(["Doc"], new string(' ', 100_000) + "a" + new string(' ', 100_000) + "\n", false)]));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        sections.Select(s => s.Path.Count).ShouldBe([1, 2], "the empty heading adds no path element");
        sections[1].Path[1].Length.ShouldBe(100_002);
        planned[1].Text.ShouldBe("a");
    }

    [Fact]
    public void Corrupt_pdf_gives_a_typed_user_safe_error()
    {
        using var garbage = new MemoryStream(Encoding.UTF8.GetBytes("this is not a pdf at all"));

        var error = Should.Throw<TextExtractionException>(() => new PdfPigTextExtractor().Extract(garbage));

        error.InnerException.ShouldNotBeNull();
        error.Message.ShouldNotContain("PdfPig");
    }

    /// <summary>The length of the longest end of <paramref name="previous"/> that is also the start of <paramref name="next"/>.</summary>
    private static int LongestSuffixPrefix(string previous, string next)
    {
        for (var length = Math.Min(previous.Length, next.Length); length > 0; length--)
        {
            if (previous.EndsWith(next[..length], StringComparison.Ordinal))
            {
                return length;
            }
        }

        return 0;
    }
}
