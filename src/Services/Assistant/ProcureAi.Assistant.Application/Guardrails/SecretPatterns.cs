using System.Text.RegularExpressions;

namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// What an answer must never contain (the output guard): credentials and personal data. <see cref="Find"/> returns the first
/// <see cref="GuardReasons"/> code that matches, or null:
/// <list type="bullet">
/// <item>connection strings (<c>Host=…;</c>, <c>Server=…;</c>, <c>Data Source=…;</c>, <c>Password=…</c>) → <see cref="GuardReasons.ConnectionString"/>;</item>
/// <item><c>-----BEGIN … PRIVATE KEY-----</c> → <see cref="GuardReasons.PrivateKey"/>; JWTs (<c>eyJ….….…</c>) → <see cref="GuardReasons.Jwt"/>;</item>
/// <item><c>sk-…</c>, <c>ghp_…</c> / <c>github_pat_…</c>, AWS <c>AKIA…</c>, and a 32+ character base64url run next to "key", "secret" or
/// "token" → <see cref="GuardReasons.ApiKey"/>;</item>
/// <item>email addresses → <see cref="GuardReasons.Email"/>;</item>
/// <item>South African ID numbers: 13 digits on their own with a valid date of birth (YYMMDD) and a valid Luhn check digit, so an amount or
/// order number of 13 digits passes → <see cref="GuardReasons.SaIdNumber"/>;</item>
/// <item>South African mobile numbers (<c>+27</c> or <c>0</c>, then 6, 7 or 8 and eight more digits, spaces or dashes allowed) →
/// <see cref="GuardReasons.MobileNumber"/>.</item>
/// </list>
/// </summary>
public static partial class SecretPatterns
{
    public static string? Find(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (ConnectionString().IsMatch(text))
        {
            return GuardReasons.ConnectionString;
        }

        if (PrivateKey().IsMatch(text))
        {
            return GuardReasons.PrivateKey;
        }

        if (Jwt().IsMatch(text))
        {
            return GuardReasons.Jwt;
        }

        if (ApiKey().IsMatch(text) || KeyNearLongRun().IsMatch(text))
        {
            return GuardReasons.ApiKey;
        }

        if (Email().IsMatch(text))
        {
            return GuardReasons.Email;
        }

        if (Thirteen().Matches(text).Any(m => IsSaIdNumber(m.Value)))
        {
            return GuardReasons.SaIdNumber;
        }

        return Mobile().IsMatch(text) ? GuardReasons.MobileNumber : null;
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

    [GeneratedRegex(@"\b(?:host|server|data source)\s*=\s*[^;\s]+\s*;|\b(?:password|pwd)\s*=\s*[^;\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionString();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\beyJ[\w-]+\.[\w-]+\.[\w-]+")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9]{20,}|\bghp_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}|\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"\b(?:key|secret|token)s?\b[^\n]{0,20}?[A-Za-z0-9_-]{32,}|[A-Za-z0-9_-]{32,}[^\n]{0,20}?\b(?:key|secret|token)s?\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeyNearLongRun();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<!\d)\d{13}(?!\d)")]
    private static partial Regex Thirteen();

    [GeneratedRegex(@"(?<![\w+])(?:\+27|0)[ -]?[6-8]\d(?:[ -]?\d){7}(?!\d)")]
    private static partial Regex Mobile();
}
