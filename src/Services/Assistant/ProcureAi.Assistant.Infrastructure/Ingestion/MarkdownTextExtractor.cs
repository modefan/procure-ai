using System.Text;
using ProcureAi.Assistant.Application.Ingestion;

namespace ProcureAi.Assistant.Infrastructure.Ingestion;

/// <summary>
/// Reads Markdown: <c>#</c> headings give the section path, pipe tables become sections of their own, everything else is text.
/// Lines inside code fences are text, never headings.
/// </summary>
public sealed class MarkdownTextExtractor : ITextExtractor
{
    public const string ContentType = "text/markdown";

    public bool Supports(string contentType) =>
        contentType.Split(';')[0].Trim().Equals(ContentType, StringComparison.OrdinalIgnoreCase);

    public ExtractedDocument Extract(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var sections = new List<Section>();
        var headings = new List<(int Level, string Title)>();
        var text = new List<string>();
        var table = new List<string>();
        var inFence = false;

        void FlushText()
        {
            var body = string.Join('\n', text).Trim();
            if (body.Length > 0)
            {
                sections.Add(new Section(headings.Select(h => h.Title).ToList(), body, false));
            }

            text.Clear();
        }

        void FlushTable()
        {
            if (table.Count > 0)
            {
                sections.Add(new Section(headings.Select(h => h.Title).ToList(), string.Join('\n', table), true));
                table.Clear();
            }
        }

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            if (!inFence && TryHeading(trimmed, out var level, out var title))
            {
                FlushTable();
                FlushText();
                headings.RemoveAll(h => h.Level >= level);
                if (title.Length > 0)
                {
                    headings.Add((level, title));
                }
            }
            else if (!inFence && trimmed.StartsWith('|'))
            {
                FlushText();
                table.Add(trimmed);
            }
            else
            {
                FlushTable();
                text.Add(line);
            }
        }

        FlushTable();
        FlushText();
        return new ExtractedDocument(sections);
    }

    private static bool TryHeading(string line, out int level, out string title)
    {
        level = 0;
        title = "";
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is 0 or > 6 || level >= line.Length || line[level] != ' ')
        {
            return false;
        }

        title = StripClosingHashes(line[level..].Trim());
        return true;
    }

    /// <summary>
    /// CommonMark: a closing run of # counts only when a space or tab precedes it, so "## Learning C#" keeps its #; a title of only #s
    /// is empty. Done by hand, not by regex: a pattern like a space-or-tab run followed by #s at the end backtracks quadratically on a long run of spaces.
    /// </summary>
    private static string StripClosingHashes(string title)
    {
        var end = title.Length;
        while (end > 0 && title[end - 1] == '#')
        {
            end--;
        }

        if (end == 0)
        {
            return "";
        }

        return end < title.Length && title[end - 1] is ' ' or '\t' ? title[..end].Trim() : title;
    }
}
