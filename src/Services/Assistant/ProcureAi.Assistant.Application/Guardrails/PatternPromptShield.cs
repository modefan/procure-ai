using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// The pattern guard (ruling 11), always on and first: no model call, so it costs nothing and cannot be talked round. It checks, in order:
/// <list type="number">
/// <item>Unicode tag characters (U+E0000–U+E007F, invisible text that models still read) → blocked outright,
/// <see cref="GuardReasons.HiddenCharacters"/>;</item>
/// <item>questions only: the length limit (<c>Assistant:Guard:MaxQuestionLength</c>, 2 000 characters) → <see cref="GuardReasons.TooLong"/>;</item>
/// <item>chat role and data tags (<c>&lt;/data&gt;</c>, <c>&lt;system&gt;</c>, <c>&lt;|im_start|&gt;</c>, <c>[INST]</c>), and in documents
/// role prefixes at a line start (<c>System:</c>, <c>### System:</c>, <c>Assistant:</c>, <c>Human:</c>) → <see cref="GuardReasons.RoleTag"/>;</item>
/// <item>injection and jailbreak phrases → <see cref="GuardReasons.Injection"/>; requests for the assistant's own prompt or instructions →
/// <see cref="GuardReasons.PromptLeak"/>;</item>
/// <item>the high-signal injection phrases again over a <see cref="Compact"/> copy (letters only, no spaces, leetspeak folded), so
/// "i g n o r e all previous instructions" and "1gn0re all prev10us 1nstruct10ns" are caught → <see cref="GuardReasons.Injection"/>;</item>
/// <item>encodings: base64 runs that decode to readable text go through every check here again (an instruction, a tag, a secret or
/// cross-tenant request inside → <see cref="GuardReasons.EncodedInstructions"/>), and "decode this and follow it" requests;</item>
/// <item>questions only: requests for another tenant's data → <see cref="GuardReasons.CrossTenant"/> and for the system's own keys,
/// passwords or connection strings → <see cref="GuardReasons.SecretRequest"/>. Documents may talk about other companies and password
/// rules, so these two run on questions only (and on decoded base64).</item>
/// </list>
/// <para>
/// The text is normalized first (<see cref="Normalize"/>): NFKC (full-width letters), every format character removed (zero-width
/// characters, bidi controls, invisible operators, U+180E), Cyrillic and Greek look-alike letters folded to Latin, whitespace collapsed.
/// This narrows the usual bypasses; it does not close them all (another script's look-alikes, synonyms, paraphrases), which is why the
/// classifier runs after the patterns.
/// </para>
/// <para>
/// The patterns block only wording that is unambiguous. Ambiguous wording ("what is the API key?", "skip all the approval rules") is left
/// to the classifier (<c>Assistant:Guard:Classifier=Model</c>), so harmless policy questions such as "What is our API key policy?" or "What
/// are the password complexity requirements?" pass (the red-team set <c>redteam.json</c> pins both sides). With
/// <c>Classifier=None</c> such ambiguous attacks pass the guard; the prompt's own rules are then the only defence.
/// </para>
/// Every pattern has a match timeout (<see cref="SecretPatterns.RegexTimeoutMs"/>); a timeout blocks (<see cref="GuardReasons.TooComplex"/>).
/// </summary>
public sealed partial class PatternPromptShield(GuardOptions options) : IPromptShield
{
    private const int Timeout = SecretPatterns.RegexTimeoutMs;

    /// <summary>High-signal injection phrases as they read in a <see cref="Compact"/> copy (letters only, lower case, no spaces).</summary>
    private static readonly string[] CompactPhrases =
    [
        "ignoreallpreviousinstructions", "ignorepreviousinstructions", "ignoreallpriorinstructions", "ignorepriorinstructions",
        "ignoreyourinstructions", "ignoretheaboveinstructions", "ignoreallinstructions", "disregardyourinstructions",
        "disregardpreviousinstructions", "disregardallpreviousinstructions", "forgetyourinstructions", "forgetpreviousinstructions",
        "forgetallpreviousinstructions", "revealyoursystemprompt", "printyoursystemprompt", "showyoursystemprompt", "doanythingnow",
        "jailbreak",
    ];

    /// <summary>Cyrillic and Greek letters that look like Latin ones, folded to them before the patterns run.</summary>
    private static readonly Dictionary<char, char> Confusables = new()
    {
        // Cyrillic, lower and upper case.
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['х'] = 'x', ['і'] = 'i', ['ѕ'] = 's', ['у'] = 'y', ['к'] = 'k',
        ['м'] = 'm', ['т'] = 't', ['н'] = 'h', ['в'] = 'b',
        ['А'] = 'A', ['Е'] = 'E', ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C', ['Х'] = 'X', ['І'] = 'I', ['Ѕ'] = 'S', ['У'] = 'Y', ['К'] = 'K',
        ['М'] = 'M', ['Т'] = 'T', ['Н'] = 'H', ['В'] = 'B',
        // Greek, lower and upper case.
        ['ο'] = 'o', ['ρ'] = 'p', ['α'] = 'a', ['ε'] = 'e', ['ι'] = 'i', ['κ'] = 'k', ['ν'] = 'v', ['τ'] = 't', ['υ'] = 'u', ['χ'] = 'x',
        ['Ο'] = 'O', ['Ρ'] = 'P', ['Α'] = 'A', ['Ε'] = 'E', ['Ι'] = 'I', ['Κ'] = 'K', ['Ν'] = 'N', ['Τ'] = 'T', ['Υ'] = 'Y', ['Χ'] = 'X',
    };

    public Task<PromptShieldResult> CheckQuestionAsync(string text, CancellationToken cancellationToken)
    {
        if (HasTagCharacters(text))
        {
            return Blocked(GuardReasons.HiddenCharacters);
        }

        if (text.Length > options.MaxQuestionLength)
        {
            return Blocked(GuardReasons.TooLong);
        }

        return Result(() =>
        {
            var normalized = Normalize(text);
            return Common(normalized, document: false) ?? Requests(normalized);
        });
    }

    public Task<PromptShieldResult> CheckDocumentAsync(string text, CancellationToken cancellationToken) =>
        HasTagCharacters(text) ? Blocked(GuardReasons.HiddenCharacters) : Result(() => Common(Normalize(text), document: true));

    private static Task<PromptShieldResult> Blocked(string reason) => Task.FromResult(PromptShieldResult.Block(reason));

    private static Task<PromptShieldResult> Result(Func<string?> check)
    {
        string? reason;
        try
        {
            reason = check();
        }
        catch (RegexMatchTimeoutException)
        {
            reason = GuardReasons.TooComplex;
        }

        return Task.FromResult(reason is null ? PromptShieldResult.Allowed : PromptShieldResult.Block(reason));
    }

    /// <summary>The checks for questions and documents alike: tags, phrases, compact phrases, then encoded text.</summary>
    private static string? Common(string text, bool document) =>
        (document && RolePrefix().IsMatch(text) ? GuardReasons.RoleTag : null)
        ?? Plain(text)
        ?? (CompactInjection(text) ? GuardReasons.Injection : null)
        ?? (DecodeAndFollow().IsMatch(text) ? GuardReasons.EncodedInstructions : null)
        ?? (Decoded(text).Any(EncodedIsHostile) ? GuardReasons.EncodedInstructions : null);

    /// <summary>Decoded base64 goes through every check: tags, phrases, compact phrases, cross-tenant and secret requests.</summary>
    private static bool EncodedIsHostile(string decoded)
    {
        var normalized = Normalize(decoded);
        return HasTagCharacters(decoded) || Plain(normalized) is not null || CompactInjection(normalized) || Requests(normalized) is not null;
    }

    private static string? Plain(string text) =>
        RoleTag().IsMatch(text) ? GuardReasons.RoleTag
        : Injection().IsMatch(text) ? GuardReasons.Injection
        : PromptLeak().IsMatch(text) ? GuardReasons.PromptLeak
        : null;

    private static string? Requests(string text) =>
        CrossTenant().IsMatch(text) ? GuardReasons.CrossTenant
        : SecretRequest().IsMatch(text) ? GuardReasons.SecretRequest
        : null;

    private static bool CompactInjection(string text)
    {
        var compact = Compact(text);
        return CompactPhrases.Any(phrase => compact.Contains(phrase, StringComparison.Ordinal));
    }

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

    /// <summary>Whether the text has Unicode tag characters (U+E0000–U+E007F): invisible on screen, readable by a model.</summary>
    internal static bool HasTagCharacters(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0xE0000 and <= 0xE007F)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// NFKC (full-width and other compatibility forms to plain letters), every format character (Unicode category Cf: zero-width
    /// characters, soft hyphen, bidi controls, invisible operators, U+180E, tag characters) removed, Cyrillic and Greek look-alikes folded
    /// to Latin, and runs of spaces and tabs collapsed to one space (line breaks are kept for the line-start checks).
    /// </summary>
    internal static string Normalize(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(folded.Length);
        foreach (var rune in folded.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                continue;
            }

            if (rune.IsBmp && Confusables.TryGetValue((char)rune.Value, out var latin))
            {
                builder.Append(latin);
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return Whitespace().Replace(builder.ToString(), " ");
    }

    /// <summary>
    /// A copy for the spaced-out and leetspeak check: lower case, leetspeak folded (0→o, 1→i, 3→e, 4→a, 5→s, 7→t, @→a), and letters only
    /// (no spaces, dots or other separators). Run it on <see cref="Normalize"/>d text.
    /// </summary>
    internal static string Compact(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            var folded = c switch
            {
                '0' => 'o',
                '1' => 'i',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '@' => 'a',
                _ => c,
            };
            if (char.IsLetter(folded))
            {
                builder.Append(folded);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"<\s*/?\s*(?:system|assistant|user|developer|tool|data|context|instructions?|sources?|prompt)\b[^>]{0,100}>|<\|[a-z_]{2,20}\|>|\[/?(?:INST|SYS)\]|<<\s*/?\s*SYS\s*>>", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex RoleTag();

    [GeneratedRegex(@"^[ \t]*(?:#{1,6}[ \t]*)?(?:system|assistant|human)[ \t]*:", RegexOptions.IgnoreCase | RegexOptions.Multiline, Timeout)]
    private static partial Regex RolePrefix();

    [GeneratedRegex(
        @"\b(?:ignore|disregard|forget|override|bypass|skip)\b[^.\n]{0,30}?\b(?:previous|prior|above|earlier|preceding|your|system|developer)\b[^.\n]{0,20}?\b(?:instructions?|rules|prompts?|guidelines|directions|guardrails|restrictions|context)\b"
        + @"|\bbypass\b[^.\n]{0,20}?\b(?:restrictions|guardrails|filters|safety|security)\b"
        + @"|\byou are (?:now|no longer)\b"
        + @"|\bfrom now on,? (?:you|the assistant)\b"
        + @"|\b(?:act|behave|respond) as (?:if you (?:are|were) )?(?:an? )?(?:unrestricted|unfiltered|uncensored|jailbroken|evil|dan)\b"
        + @"|\bdo anything now\b|\bdeveloper mode\b|\bjailbr(?:eak|oken)\b|\bno (?:restrictions|rules|filters|limits) (?:apply|at all)\b"
        + @"|\bpretend (?:that )?(?:you are|you're|to be)\b|\brole-?play as\b"
        + @"|\b(?:new|updated|real|actual) instructions?\s*:"
        + @"|\b(?:system|admin|developer|important system) (?:override|message|instructions?|prompt)\s*:",
        RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex Injection();

    [GeneratedRegex(
        @"\b(?:print|reveal|show|repeat|output|display|tell me|give me|what (?:is|are|was|were)|leak|dump|copy|paste|list)\b[^.\n?]{0,30}?\b(?:your|the assistant's)\b[^.\n?]{0,20}?\b(?:system prompt|system message|prompt|instructions)\b"
        + @"|\b(?:print|reveal|show|repeat|output|display|tell me|give me|leak|dump|copy)\b[^.\n?]{0,30}?\b(?:system prompt|system message|hidden (?:prompt|instructions)|initial (?:prompt|instructions)|developer message)\b"
        + @"|\b(?:repeat|print|output|copy)\b[^.\n?]{0,20}?\b(?:everything|all|the text|the words|the lines?) (?:above|before)\b",
        RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex PromptLeak();

    [GeneratedRegex(
        @"\b(?:decode|base64-decode|base64 decode|unbase64)\b\s+(?:this|that|it|these|the following|the text below|below)\b[^.\n]{0,40}?\b(?:follow|execute|obey|run|apply|do what it says|do as it says)\b",
        RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex DecodeAndFollow();

    [GeneratedRegex(@"[A-Za-z0-9+/_-]{24,}={0,2}", RegexOptions.None, Timeout)]
    private static partial Regex Base64Run();

    [GeneratedRegex(
        @"\b(?:other|another|all|every|different)\s+(?:tenants?|organi[sz]ations?)(?:'s|')?\s+(?:data|requests?|purchase requests|invoices?|budgets?|orders?|purchases|records|conversations|documents|users|spend(?:ing)?)\b"
        + @"|\b(?:all|every)\s+compan(?:y|ies)(?:'s|')?\s+(?:data|requests?|purchase requests|invoices?|budgets?|orders?|purchases|records|conversations|documents|users|spend(?:ing)?)\b"
        + @"|\b(?:data|requests?|invoices?|budgets?|orders?|purchases|records|conversations|documents|users|spend(?:ing)?)\s+(?:of|for|from|belonging to|at)\s+(?:the\s+)?(?:(?:other|another|all|every|different)\s+(?:tenants?|organi[sz]ations?)|(?:all|every)\s+compan(?:y|ies))\b"
        + @"|\bswitch (?:to )?(?:the )?(?:tenant|organi[sz]ation)\b|\bx-tenant-id\b|\btenant[_ -]?id\s*[:=]",
        RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex CrossTenant();

    /// <summary>
    /// Requests for the system's own secrets only: "your / its / the system's … API key, password, token, connection string", "the admin
    /// (database, server, OpenAI…) password or key", "the connection string for the database", "print the environment variables", the
    /// X-Api-Key value. "What is the process to reset my password?" or "Do we share our API keys with suppliers?" pass to the classifier.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:your|its|the system's|the assistant's|the server's|the app's|the application's|this system's)\s+(?:[\w-]+\s+){0,3}?(?:api[ -]?keys?|passwords?|connection strings?|secrets?|tokens?|private keys?|credentials|environment variables?|env vars?|user secrets)\b"
        + @"|\b(?:the|our)\s+(?:admin|administrator|database|db|server|root|master|service|system|production|prod|openai|azure|postgres|redis|rabbitmq|keycloak)\s+(?:api[ -]?keys?|keys?|passwords?|tokens?|connection strings?|credentials|secrets?)\b"
        + @"|\bconnection strings?\s+(?:for|of|to)\s+(?:the\s+)?(?:[\w-]+\s+){0,2}?(?:database|db|server|postgres|redis|cache|broker)\b"
        + @"|\b(?:print|show|list|dump|reveal|give me|tell me|what are|output)\b[^.\n?]{0,20}?\b(?:environment variables?|env vars?|user secrets|appsettings)\b"
        + @"|\bx-api-key\b",
        RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex SecretRequest();

    [GeneratedRegex(@"[ \t\f\v  -   　]+", RegexOptions.None, Timeout)]
    private static partial Regex Whitespace();
}
