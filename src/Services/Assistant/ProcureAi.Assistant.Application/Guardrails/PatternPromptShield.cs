using System.Text;
using System.Text.RegularExpressions;

namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// The pattern guard (ruling 11), always on and first: no model call, so it costs nothing and cannot be talked round. It checks, in order:
/// <list type="number">
/// <item>questions only: the length limit (<c>Assistant:Guard:MaxQuestionLength</c>, 2 000 characters) → <see cref="GuardReasons.TooLong"/>;</item>
/// <item>chat role and data tags (<c>&lt;/data&gt;</c>, <c>&lt;system&gt;</c>, <c>&lt;|im_start|&gt;</c>, <c>[INST]</c>) → <see cref="GuardReasons.RoleTag"/>;</item>
/// <item>known injection and jailbreak phrases → <see cref="GuardReasons.Injection"/>; requests for the assistant's instructions →
/// <see cref="GuardReasons.PromptLeak"/>;</item>
/// <item>encodings: base64 runs that decode to readable text are checked again (an instruction or tag inside →
/// <see cref="GuardReasons.EncodedInstructions"/>), and "decode this and follow it" requests;</item>
/// <item>questions only: requests for another tenant's data → <see cref="GuardReasons.CrossTenant"/> and for keys, passwords or other
/// secrets → <see cref="GuardReasons.SecretRequest"/>. Documents may talk about other companies and password rules, so these two run on
/// questions only.</item>
/// </list>
/// The text is normalized first (NFKC, zero-width characters removed, whitespace collapsed), so full-width letters or hidden characters do
/// not slip past. The phrases are tuned against the red-team set (<c>redteam.json</c>): ten harmless policy questions such as "What is our
/// API key policy?" must pass.
/// </summary>
public sealed partial class PatternPromptShield(GuardOptions options) : IPromptShield
{
    /// <summary>The shortest base64 run that is decoded and checked (18 bytes of text).</summary>
    private const int MinEncodedLength = 24;

    public Task<PromptShieldResult> CheckQuestionAsync(string text, CancellationToken cancellationToken)
    {
        if (text.Length > options.MaxQuestionLength)
        {
            return Task.FromResult(PromptShieldResult.Block(GuardReasons.TooLong));
        }

        var normalized = Normalize(text);
        var reason = Common(normalized)
            ?? (CrossTenant().IsMatch(normalized) ? GuardReasons.CrossTenant : null)
            ?? (SecretRequest().IsMatch(normalized) ? GuardReasons.SecretRequest : null);
        return Task.FromResult(reason is null ? PromptShieldResult.Allowed : PromptShieldResult.Block(reason));
    }

    public Task<PromptShieldResult> CheckDocumentAsync(string text, CancellationToken cancellationToken)
    {
        var reason = Common(Normalize(text));
        return Task.FromResult(reason is null ? PromptShieldResult.Allowed : PromptShieldResult.Block(reason));
    }

    /// <summary>The checks for questions and documents alike: tags, phrases, then encoded text.</summary>
    private static string? Common(string text) =>
        Plain(text)
        ?? (DecodeAndFollow().IsMatch(text) ? GuardReasons.EncodedInstructions : null)
        ?? (Decoded(text).Any(decoded => Plain(Normalize(decoded)) is not null) ? GuardReasons.EncodedInstructions : null);

    private static string? Plain(string text) =>
        RoleTag().IsMatch(text) ? GuardReasons.RoleTag
        : Injection().IsMatch(text) ? GuardReasons.Injection
        : PromptLeak().IsMatch(text) ? GuardReasons.PromptLeak
        : null;

    /// <summary>The readable text of every base64 (or base64url) run of the text.</summary>
    private static IEnumerable<string> Decoded(string text)
    {
        foreach (Match run in Base64Run().Matches(text))
        {
            var value = run.Value.Replace('-', '+').Replace('_', '/').TrimEnd('=');
            value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
            var buffer = new byte[value.Length * 3 / 4];
            if (!Convert.TryFromBase64String(value, buffer, out var written))
            {
                continue;
            }

            string decoded;
            try
            {
                decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, written);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            // Readable text only: random bytes (a hash, an ID) rarely decode to mostly letters and spaces.
            if (decoded.Length > 0 && decoded.Count(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c) || c is '<' or '>' or '|')
                >= decoded.Length * 0.9)
            {
                yield return decoded;
            }
        }
    }

    /// <summary>NFKC (full-width and other look-alike forms to plain letters), no zero-width characters, single spaces.</summary>
    internal static string Normalize(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (c is '​' or '‌' or '‍' or '⁠' or '﻿' or '­')
            {
                continue;
            }

            builder.Append(c);
        }

        return Whitespace().Replace(builder.ToString(), " ");
    }

    [GeneratedRegex(@"<\s*/?\s*(?:system|assistant|user|developer|tool|data|context|instructions?|sources?|prompt)\b[^>]{0,100}>|<\|[a-z_]{2,20}\|>|\[/?(?:INST|SYS)\]|<<\s*/?\s*SYS\s*>>", RegexOptions.IgnoreCase)]
    private static partial Regex RoleTag();

    [GeneratedRegex(
        @"\b(?:ignore|disregard|forget|override|bypass|skip)\b[^.\n]{0,30}?\b(?:previous|prior|above|earlier|preceding|all|any|your|system|developer)\b[^.\n]{0,20}?\b(?:instructions?|rules|prompts?|guidelines|directions|guardrails|restrictions|context)\b"
        + @"|\byou are (?:now|no longer)\b"
        + @"|\bfrom now on,? (?:you|the assistant)\b"
        + @"|\b(?:act|behave|respond) as (?:if you (?:are|were) )?(?:an? )?(?:unrestricted|unfiltered|uncensored|jailbroken|evil|dan)\b"
        + @"|\bdo anything now\b|\bdeveloper mode\b|\bjailbr(?:eak|oken)\b|\bno (?:restrictions|rules|filters|limits) (?:apply|at all)\b"
        + @"|\bpretend (?:that )?(?:you are|you're|to be)\b|\brole-?play as\b"
        + @"|\b(?:new|updated|real|actual) instructions?\s*:"
        + @"|\b(?:system|admin|developer|important system) (?:override|message|instructions?|prompt)\s*:",
        RegexOptions.IgnoreCase)]
    private static partial Regex Injection();

    [GeneratedRegex(
        @"\b(?:print|reveal|show|repeat|output|display|tell me|give me|what (?:is|are|was|were)|leak|dump|copy|paste|list)\b[^.\n?]{0,30}?\b(?:your|the assistant's|its)\b[^.\n?]{0,20}?\b(?:system prompt|system message|prompt|instructions|rules)\b"
        + @"|\b(?:print|reveal|show|repeat|output|display|tell me|give me|leak|dump|copy)\b[^.\n?]{0,30}?\b(?:system prompt|system message|hidden (?:prompt|instructions)|initial (?:prompt|instructions)|developer message)\b"
        + @"|\b(?:repeat|print|output|copy)\b[^.\n?]{0,20}?\b(?:everything|all|the text|the words|the lines?) (?:above|before)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PromptLeak();

    [GeneratedRegex(@"\b(?:decode|base64-decode|base64 decode|unbase64)\b[^.\n]{0,40}?\b(?:follow|execute|do|run|obey|apply)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DecodeAndFollow();

    [GeneratedRegex(@"[A-Za-z0-9+/_-]{24,}={0,2}")]
    private static partial Regex Base64Run();

    [GeneratedRegex(
        @"\b(?:other|another|all|every|different)\s+(?:tenants?|compan(?:y|ies)|organi[sz]ations?)(?:'s|')?\s+(?:data|requests?|purchase requests|invoices?|budgets?|orders?|purchases|records|conversations|documents|users|spend(?:ing)?)\b"
        + @"|\b(?:data|requests?|invoices?|budgets?|orders?|purchases|records|conversations|documents|users|spend(?:ing)?)\s+(?:of|for|from|belonging to|at)\s+(?:the\s+)?(?:other|another|all|every|different)\s+(?:tenants?|compan(?:y|ies)|organi[sz]ations?)\b"
        + @"|\bswitch (?:to )?(?:the )?(?:tenant|company|organi[sz]ation)\b|\bx-tenant-id\b|\btenant[_ -]?id\s*[:=]",
        RegexOptions.IgnoreCase)]
    private static partial Regex CrossTenant();

    [GeneratedRegex(
        @"\b(?:give|show|tell|send|print|reveal|list|dump|share|leak|read|what(?:'s| is| are| was))\b(?: me)?[^.\n?]{0,30}?\b(?:your|the|our|its|all|any)\b[^.\n?]{0,25}?\b(?:api[ -]?keys?|passwords?|connection strings?|secrets?|tokens?|private keys?|credentials|environment variables?|env vars?|user secrets)\b(?!\s+(?:polic(?:y|ies)|rules?|rotation|requirements?|guidelines?|standards?|length|expiry|expiration|of appreciation))"
        + @"|\bx-api-key\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretRequest();

    [GeneratedRegex(@"[ \t\f\v  -   　]+")]
    private static partial Regex Whitespace();
}
