using System.Text;
using System.Text.RegularExpressions;
using ProcureAi.Assistant.Application.Ingestion;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ProcureAi.Assistant.Infrastructure.Ingestion;

/// <summary>
/// Reads a PDF with PdfPig (text PDFs only; a scanned PDF has no text and gives no sections). A line is a heading when it starts with a
/// number such as <c>4</c> or <c>4.2</c> and is set in a larger font than the body median; the number's depth is the heading's level.
/// An unnumbered larger line before the first heading is the document title. Lines with at least two runs of three or more spaces
/// (a big gap between words is read as spaces) on three or more consecutive lines are a table and stay whole.
/// </summary>
public sealed partial class PdfPigTextExtractor : ITextExtractor
{
    public const string ContentType = "application/pdf";

    /// <summary>A heading must be this many points larger than the body median.</summary>
    private const double HeadingExtraPoints = 0.5;

    /// <summary>A gap between two words wider than this many ems is a column gap, written as three spaces.</summary>
    private const double ColumnGapEms = 0.8;

    /// <summary>A vertical step this many times the usual line step starts a new paragraph.</summary>
    private const double ParagraphStepFactor = 1.2;

    private const int MinTableLines = 3;

    public bool Supports(string contentType) =>
        contentType.Split(';')[0].Trim().Equals(ContentType, StringComparison.OrdinalIgnoreCase);

    public ExtractedDocument Extract(Stream content)
    {
        try
        {
            return Read(content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // PdfPig has no common exception type for damaged, encrypted or non-PDF input; its message is for the log, not the user.
            throw new TextExtractionException("The PDF could not be read. It may be damaged, password protected or not a PDF.", ex);
        }
    }

    private static ExtractedDocument Read(Stream content)
    {
        using var pdf = PdfDocument.Open(content);
        var lines = pdf.GetPages().SelectMany(ReadLines).ToList();
        if (lines.Count == 0)
        {
            return new ExtractedDocument([]);
        }

        var bodySize = Median(lines.Select(l => l.Size));
        var step = Median(lines.Zip(lines.Skip(1)).Where(p => p.First.Page == p.Second.Page && p.First.Bottom > p.Second.Bottom)
            .Select(p => p.First.Bottom - p.Second.Bottom), fallback: bodySize * 1.2);

        var sections = new List<Section>();
        var pending = new List<(string Text, bool NewParagraph)>();
        string? title = null;
        var numbered = new List<string>();
        PdfLine? previous = null;
        var afterHeading = false;

        List<string> Path() => (title is null ? numbered : [title, .. numbered]);

        void Flush()
        {
            sections.AddRange(ToSections(pending, Path()));
            pending.Clear();
        }

        foreach (var line in lines)
        {
            var larger = line.Size >= bodySize + HeadingExtraPoints;
            if (larger && NumberedHeading().Match(line.Text) is { Success: true } heading)
            {
                Flush();
                var depth = heading.Groups[1].Value.Count(c => c == '.') + 1;
                numbered = [.. numbered.Take(depth - 1), Collapse(line.Text)];
                afterHeading = true;
            }
            else if (larger && title is null && numbered.Count == 0 && pending.Count == 0 && sections.Count == 0)
            {
                title = Collapse(line.Text);
                afterHeading = true;
            }
            else
            {
                var newParagraph = previous is null || afterHeading
                    || (previous.Page == line.Page && previous.Bottom - line.Bottom > step * ParagraphStepFactor);
                pending.Add((line.Text, newParagraph));
                afterHeading = false;
            }

            previous = line;
        }

        Flush();
        return new ExtractedDocument(sections);
    }

    private static IEnumerable<PdfLine> ReadLines(Page page)
    {
        var words = page.GetWords().Where(w => w.Letters.Count > 0)
            .OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left).ToList();
        var current = new List<Word>();
        foreach (var word in words)
        {
            if (current.Count > 0 && Math.Abs(current[0].BoundingBox.Bottom - word.BoundingBox.Bottom) > SizeOf(current[0]) * 0.4)
            {
                yield return ToLine(current, page.Number);
                current.Clear();
            }

            current.Add(word);
        }

        if (current.Count > 0)
        {
            yield return ToLine(current, page.Number);
        }
    }

    private static PdfLine ToLine(List<Word> words, int page)
    {
        var ordered = words.OrderBy(w => w.BoundingBox.Left).ToList();
        var size = ordered.Average(SizeOf);
        var text = new StringBuilder(ordered[0].Text);
        for (var i = 1; i < ordered.Count; i++)
        {
            var gap = ordered[i].BoundingBox.Left - ordered[i - 1].BoundingBox.Right;
            text.Append(gap > size * ColumnGapEms ? "   " : " ").Append(ordered[i].Text);
        }

        return new PdfLine(text.ToString(), Math.Round(size, 1), ordered[0].BoundingBox.Bottom, page);
    }

    private static double SizeOf(Word word) => word.Letters.Average(l => l.PointSize);

    private static double Median(IEnumerable<double> values, double fallback = 0)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? fallback : sorted[sorted.Count / 2];
    }

    /// <summary>Turns the lines under one heading into text and table sections.</summary>
    private static IEnumerable<Section> ToSections(List<(string Text, bool NewParagraph)> lines, List<string> path)
    {
        var text = new StringBuilder();
        var i = 0;

        Section? TakeText()
        {
            var body = text.ToString().Trim();
            text.Clear();
            return body.Length == 0 ? null : new Section(path, body, false);
        }

        while (i < lines.Count)
        {
            var run = 0;
            while (i + run < lines.Count && IsTableLine(lines[i + run].Text))
            {
                run++;
            }

            if (run >= MinTableLines)
            {
                if (TakeText() is { } before)
                {
                    yield return before;
                }

                yield return new Section(path, string.Join('\n', lines.Skip(i).Take(run).Select(l => ToRow(l.Text))), true);
                i += run;
                continue;
            }

            var (line, newParagraph) = lines[i];
            text.Append(text.Length == 0 ? "" : newParagraph ? "\n\n" : " ").Append(Collapse(line));
            i++;
        }

        if (TakeText() is { } last)
        {
            yield return last;
        }
    }

    /// <summary>A table row with its cells joined by <c> | </c>, like a Markdown table, so the columns survive whitespace normalisation.</summary>
    private static string ToRow(string text) => string.Join(" | ", ColumnGap().Split(text.Trim()).Select(Collapse));

    private static bool IsTableLine(string text) => ColumnGap().Matches(text.Trim()).Count >= 2;

    private static string Collapse(string text) => WhiteSpace().Replace(text, " ").Trim();

    private sealed record PdfLine(string Text, double Size, double Bottom, int Page);

    [GeneratedRegex(@"^(\d+(?:\.\d+)*)\s+\S")]
    private static partial Regex NumberedHeading();

    [GeneratedRegex(@" {3,}")]
    private static partial Regex ColumnGap();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
