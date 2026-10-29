namespace ProcureAi.Assistant.Application.Ingestion;

/// <summary>Turns an uploaded file into sections of text that keep the document's heading structure.</summary>
public interface ITextExtractor
{
    /// <summary>Whether this extractor reads files of the content type (parameters such as <c>; charset=utf-8</c> are ignored).</summary>
    bool Supports(string contentType);

    /// <summary>
    /// Reads the file. A file without text (an empty or scanned PDF) gives a document without sections.
    /// </summary>
    /// <exception cref="TextExtractionException">The file is damaged, encrypted or not of this type.</exception>
    ExtractedDocument Extract(Stream content);
}

/// <summary>The text of one document, in reading order.</summary>
public sealed record ExtractedDocument(IReadOnlyList<Section> Sections);

/// <summary>
/// A run of text under one heading. <paramref name="Path"/> is the headings from the document title down, e.g.
/// <c>["Procurement Policy", "4 Approvals", "4.2 Approval thresholds"]</c>. A table is its own section with <paramref name="IsTable"/> set
/// and the same path as the text around it; it is never split.
/// </summary>
public sealed record Section(IReadOnlyList<string> Path, string Text, bool IsTable);
