using System.Globalization;
using System.Text.RegularExpressions;

namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// The output guard of one streamed answer: <see cref="Inspect"/> checks each update together with the last <c>window</c> characters
/// before it (<c>Assistant:Guard:OutputWindow</c>, 200), so a secret split across two updates is still caught (<see cref="SecretPatterns"/>).
/// One instance per answer; not thread-safe. <see cref="IsGrounded"/> is the grounding rule (ruling 10), run by the answer flow after
/// <c>done</c>.
/// </summary>
public sealed partial class OutputGuard(int window)
{
    private string _tail = "";

    /// <summary>The <see cref="GuardReasons"/> code of a secret in the window plus <paramref name="update"/>, or null.</summary>
    public string? Inspect(string update)
    {
        if (string.IsNullOrEmpty(update))
        {
            return null;
        }

        var text = _tail + update;
        var reason = SecretPatterns.Find(text);
        _tail = text.Length <= window ? text : text[^window..];
        return reason;
    }

    /// <summary>
    /// Ruling 10: an answer is grounded when it cites at least one source that exists (<c>[S1]</c> … <c>[S<paramref name="sourceCount"/>]</c>)
    /// or when at least one tool result was used.
    /// </summary>
    public static bool IsGrounded(string answer, int sourceCount, int toolResultCount)
    {
        if (toolResultCount > 0)
        {
            return true;
        }

        foreach (Match citation in Citation().Matches(answer ?? ""))
        {
            if (int.TryParse(citation.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number >= 1 && number <= sourceCount)
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\[S(?<n>\d{1,3})\]")]
    private static partial Regex Citation();
}
