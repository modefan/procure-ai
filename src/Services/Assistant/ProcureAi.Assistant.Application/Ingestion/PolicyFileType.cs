using System.Text;

namespace ProcureAi.Assistant.Application.Ingestion;

/// <summary>
/// What an uploaded file really is (ruling 12): a PDF (<c>.pdf</c> that starts with <c>%PDF-</c>) or Markdown (<c>.md</c> or
/// <c>.markdown</c> that is valid UTF-8 text without NUL bytes). The extension alone is never trusted: an executable renamed to
/// <c>.pdf</c> is refused.
/// </summary>
public static class PolicyFileType
{
    public const string Pdf = "application/pdf";
    public const string Markdown = "text/markdown";

    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The content type of the file, or null when it is neither a real PDF nor a real Markdown file.</summary>
    public static string? Detect(string fileName, ReadOnlySpan<byte> content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".pdf" when content.StartsWith(PdfMagic) => Pdf,
            ".md" or ".markdown" when IsText(content) => Markdown,
            _ => null,
        };
    }

    private static bool IsText(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty || content.Contains((byte)0))
        {
            return false;
        }

        try
        {
            StrictUtf8.GetCharCount(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
