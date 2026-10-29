namespace ProcureAi.Assistant.Application.Ingestion;

/// <summary>
/// A file could not be read (damaged, encrypted, not really a PDF). <see cref="Exception.Message"/> is safe to show to the user; the
/// library's own error is the inner exception and goes to the log only.
/// </summary>
public sealed class TextExtractionException : Exception
{
    public TextExtractionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
