using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Ingestion;

/// <summary>A chunk the planner wants stored: a parent (a whole section) or a child (what is embedded and searched).</summary>
/// <param name="Ordinal">The chunk's position in the plan, 0, 1, 2 ...; a parent comes before its children.</param>
/// <param name="Kind">Parent or child.</param>
/// <param name="ParentOrdinal">The parent's <paramref name="Ordinal"/> for a child; null for a parent.</param>
/// <param name="SectionPath">The headings above the text.</param>
/// <param name="Text">The normalised text.</param>
/// <param name="ContentHash">
/// Hex SHA-256 of the section path joined with <c> &gt; </c>, a newline and the normalised text. It does not include the kind: the parent
/// and the only child of a short section have the same path and text, hence the same hash. That is intended. Reuse of an embedding or
/// context is keyed by (kind, content hash, embedding model), and only children are embedded.
/// </param>
public sealed record PlannedChunk(
    int Ordinal, ChunkKind Kind, int? ParentOrdinal, IReadOnlyList<string> SectionPath, string Text, string ContentHash);

/// <summary>
/// Plans the chunks of an <see cref="ExtractedDocument"/>: one parent per section (consecutive pieces under the same heading path form one
/// section) and children of <see cref="ChunkingOptions.MinTokens"/> to <see cref="ChunkingOptions.MaxTokens"/>. A section under the maximum
/// is a single child; a table is a single child however long; longer text is split at the last paragraph break in the allowed window,
/// else the last sentence end, else the last word break, and each child repeats the end of the one before it.
/// </summary>
public sealed partial class ChunkPlanner
{
    private const int CharsPerToken = 4;

    private readonly int _minChars;
    private readonly int _maxChars;
    private readonly int _overlapChars;
    private readonly int _maxParentChars;

    public ChunkPlanner(ChunkingOptions options)
    {
        _minChars = options.MinTokens * CharsPerToken;
        _maxChars = options.MaxTokens * CharsPerToken;
        _overlapChars = _maxChars * options.OverlapPercent / 100;
        _maxParentChars = options.MaxParentTokens * CharsPerToken;
    }

    public IReadOnlyList<PlannedChunk> Plan(ExtractedDocument document)
    {
        var chunks = new List<PlannedChunk>();
        foreach (var group in GroupBySection(document.Sections))
        {
            var pieces = group.Select(s => (Text: Normalise(s.Text), s.IsTable)).Where(p => p.Text.Length > 0).ToList();
            if (pieces.Count == 0)
            {
                continue;
            }

            var path = group[0].Path;
            foreach (var window in ParentWindows(pieces))
            {
                var parentOrdinal = chunks.Count;
                var parentText = window.Text;
                chunks.Add(new PlannedChunk(parentOrdinal, ChunkKind.Parent, null, path, parentText, Hash(path, parentText)));
                foreach (var child in window.Children)
                {
                    chunks.Add(new PlannedChunk(chunks.Count, ChunkKind.Child, parentOrdinal, path, child, Hash(path, child)));
                }
            }
        }

        return chunks;
    }

    /// <summary>
    /// Cuts a section into parents: the whole section when it fits <c>MaxParentTokens</c>, else windows of whole children (a table is
    /// its own window when it alone is over the cap). Neighbouring windows share the overlap of the children where they meet.
    /// </summary>
    private List<(string Text, List<string> Children)> ParentWindows(List<(string Text, bool IsTable)> pieces)
    {
        var windows = new List<(string Text, List<string> Children)>();
        var parts = new List<string>();
        var children = new List<string>();
        var covered = 0;
        var pieceStart = 0;
        var pieceEnd = 0;
        var openPiece = -1;

        void Close()
        {
            if (openPiece >= 0)
            {
                parts.Add(pieces[openPiece].Text[pieceStart..pieceEnd].Trim());
            }

            if (children.Count > 0)
            {
                windows.Add((string.Join("\n\n", parts), children));
            }

            parts = [];
            children = [];
            covered = 0;
            openPiece = -1;
        }

        for (var p = 0; p < pieces.Count; p++)
        {
            var (text, isTable) = pieces[p];
            foreach (var (start, end) in isTable ? [(0, text.Length)] : Split(text))
            {
                var sameWindowPiece = openPiece == p;
                var added = sameWindowPiece ? end - pieceEnd : end - start + (children.Count > 0 ? 2 : 0);
                if (children.Count > 0 && covered + added > _maxParentChars)
                {
                    Close();
                    added = end - start;
                    sameWindowPiece = false;
                }

                if (!sameWindowPiece)
                {
                    if (openPiece >= 0)
                    {
                        parts.Add(pieces[openPiece].Text[pieceStart..pieceEnd].Trim());
                    }

                    openPiece = p;
                    pieceStart = start;
                }

                pieceEnd = end;
                covered += added;
                children.Add(text[start..end].Trim());
            }
        }

        Close();
        return windows;
    }

    private static List<List<Section>> GroupBySection(IReadOnlyList<Section> sections)
    {
        var groups = new List<List<Section>>();
        foreach (var section in sections)
        {
            if (groups.Count > 0 && groups[^1][0].Path.SequenceEqual(section.Path))
            {
                groups[^1].Add(section);
            }
            else
            {
                groups.Add([section]);
            }
        }

        return groups;
    }

    /// <summary>Cuts normalised text into spans of <c>_minChars.._maxChars</c>, each starting inside the end of the one before.</summary>
    private List<(int Start, int End)> Split(string text)
    {
        if (text.Length <= _maxChars)
        {
            return [(0, text.Length)];
        }

        var result = new List<(int Start, int End)>();
        var start = 0;
        while (true)
        {
            if (text.Length - start <= _maxChars)
            {
                result.Add((start, text.Length));
                return result;
            }

            // The next child starts about _overlapChars before this one's end, so end must leave it at least _minChars of text.
            var high = Math.Min(start + _maxChars, text.Length - _minChars + _overlapChars);
            var end = FindBreak(text, start + _minChars, high);
            result.Add((start, end));
            start = NextStart(text, start, end);
        }
    }

    /// <summary>The best place to end a child in <c>[low, high]</c>: the last paragraph break, else sentence end, else word break.</summary>
    private static int FindBreak(string text, int low, int high)
    {
        var window = text.AsSpan(low, high - low);
        var paragraph = window.LastIndexOf("\n\n");
        if (paragraph > 0)
        {
            return low + paragraph;
        }

        for (var i = window.Length - 1; i > 0; i--)
        {
            if (char.IsWhiteSpace(window[i]) && window[i - 1] is '.' or '!' or '?' or ':' or ';')
            {
                return low + i;
            }
        }

        var space = window.LastIndexOfAny(' ', '\n');
        return space > 0 ? low + space : high;
    }

    private int NextStart(string text, int start, int end)
    {
        if (_overlapChars == 0)
        {
            return end;
        }

        var next = Math.Max(end - _overlapChars, start + 1);

        // Begin on a word, not in the middle of one.
        while (next < end && !char.IsWhiteSpace(text[next]))
        {
            next++;
        }

        while (next < end && char.IsWhiteSpace(text[next]))
        {
            next++;
        }

        return next;
    }

    /// <summary>Trims, makes line ends <c>\n</c>, collapses spaces and tabs, and allows at most one blank line.</summary>
    internal static string Normalise(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => SpaceRuns().Replace(line, " ").Trim());
        return BlankRuns().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    private static string Hash(IReadOnlyList<string> path, string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(" > ", path) + "\n" + text)));

    [GeneratedRegex(@"[\p{Zs}\t]+")]
    private static partial Regex SpaceRuns();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();
}
