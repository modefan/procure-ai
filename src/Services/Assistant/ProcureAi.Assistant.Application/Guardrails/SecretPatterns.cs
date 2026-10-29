using System.Text.RegularExpressions;

namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// What an answer must never contain (the output guard): credentials and personal data. <see cref="Find"/> returns the first
/// <see cref="GuardReasons"/> code that matches, or null:
/// <list type="bullet">
/// <item>connection strings (<c>Host=…;</c>, <c>Server=…;</c>, <c>Data Source=…;</c>, <c>Password=…</c>) → <see cref="GuardReasons.ConnectionString"/>;</item>
/// <item><c>-----BEGIN … PRIVATE KEY-----</c> → <see cref="GuardReasons.PrivateKey"/>;</item>
/// <item>JWTs: already on their first 19 characters (<c>eyJ</c> and 16 base64url characters: the start of a JSON object in base64), so a
/// streamed token is caught long before it is complete → <see cref="GuardReasons.Jwt"/>;</item>
/// <item><c>sk-…</c>, <c>ghp_…</c> / <c>github_pat_…</c>, AWS <c>AKIA…</c>, and a 32+ character base64url run next to "key", "secret" or
/// "token" → <see cref="GuardReasons.ApiKey"/>;</item>
/// <item>email addresses → <see cref="GuardReasons.Email"/>;</item>
/// <item>South African ID numbers: 13 digits on their own with a valid date of birth (YYMMDD) and a valid Luhn check digit, so an amount or
/// order number of 13 digits passes → <see cref="GuardReasons.SaIdNumber"/>;</item>
/// <item>South African mobile numbers (<c>+27</c> or <c>0</c>, then 6, 7 or 8 and eight more digits, spaces or dashes allowed) →
/// <see cref="GuardReasons.MobileNumber"/>.</item>
/// </list>
/// Every pattern has a match timeout (<see cref="RegexTimeoutMs"/>); a timeout counts as a hit (<see cref="GuardReasons.TooComplex"/>, fail
/// closed).
/// </summary>
public static partial class SecretPatterns
{
    /// <summary>
    /// The longest text a pattern needs before it matches, for the patterns of bounded length: a 32-character run with "secrets" up to 20
    /// characters before it (59). The streaming guard's hold-back buffer (<c>Assistant:Guard:HoldBackChars</c>) must be at least this, so a
    /// secret is caught before any of it is passed on. Unbounded parts (a long host name, a long email local part) may reach past it.
    /// </summary>
    public const int LongestBoundedMatch = 59;

    /// <summary>The match timeout of every guard pattern.</summary>
    public const int RegexTimeoutMs = 250;

    public static string? Find(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            return ConnectionString().IsMatch(text) ? GuardReasons.ConnectionString
                : PrivateKey().IsMatch(text) ? GuardReasons.PrivateKey
                : Jwt().IsMatch(text) ? GuardReasons.Jwt
                : ApiKey().IsMatch(text) || KeyNearLongRun().IsMatch(text) ? GuardReasons.ApiKey
                : Email().IsMatch(text) ? GuardReasons.Email
                : Thirteen().Matches(text).Any(m => IsSaIdNumber(m.Value)) ? GuardReasons.SaIdNumber
                : Mobile().IsMatch(text) ? GuardReasons.MobileNumber
                : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return GuardReasons.TooComplex;
        }
    }

    /// <summary>A South African ID number: YYMMDD of a real month and day, then seven digits, the last a Luhn check digit.</summary>
    internal static bool IsSaIdNumber(string digits)
    {
        if (digits.Length != 13 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var month = (digits[2] - '0') * 10 + (digits[3] - '0');
        var day = (digits[4] - '0') * 10 + (digits[5] - '0');
        if (month is < 1 or > 12 || day is < 1 or > 31)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var digit = digits[12 - i] - '0';
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"\b(?:host|server|data source)\s*=\s*[^;\s]+\s*;|\b(?:password|pwd)\s*=\s*[^;\s]+", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex ConnectionString();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{16}", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9]{20}|\bghp_[A-Za-z0-9]{20}|\bgithub_pat_[A-Za-z0-9_]{20}|\bAKIA[0-9A-Z]{16}\b", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"\b(?:key|secret|token)s?\b[^\n]{0,20}?[A-Za-z0-9_-]{32}|[A-Za-z0-9_-]{32,}[^\n]{0,20}?\b(?:key|secret|token)s?\b", RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex KeyNearLongRun();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<!\d)\d{13}(?!\d)", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex Thirteen();

    [GeneratedRegex(@"(?<![\w+])(?:\+27|0)[ -]?[6-8]\d(?:[ -]?\d){7}(?!\d)", RegexOptions.None, RegexTimeoutMs)]
    private static partial Regex Mobile();
}
