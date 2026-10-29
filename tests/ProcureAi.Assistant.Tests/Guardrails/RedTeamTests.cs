using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureAi.Assistant.Application.Guardrails;
using ProcureAi.Assistant.Infrastructure.Ai.Fake;
using ProcureAi.Assistant.Infrastructure.Persistence;
using Shouldly;

namespace ProcureAi.Assistant.Tests.Guardrails;

/// <summary>
/// The red-team set (<c>Guardrails/redteam.json</c>) through the guard as it runs under the Fake provider: the patterns, then the fake
/// classifier on the <c>utility</c> model. Entries whose kind ends in <c>_document</c> go through the document check, the rest are questions.
/// </summary>
public sealed class RedTeamTests
{
    public sealed record Entry(string Text, string Expected, string Kind)
    {
        public bool IsDocument => Kind.EndsWith("_document", StringComparison.Ordinal);

        public override string ToString() => $"{Kind}: {(Text.Length <= 60 ? Text : Text[..60] + "…")}";
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<Entry> Entries { get; } =
        JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Guardrails", "redteam.json")), Json)!;

    public static TheoryData<Entry> All => [.. Entries];

    /// <summary>The composition registered under the Fake provider with <c>Assistant:Guard:Classifier=Model</c>.</summary>
    internal static IPromptShield Shield() =>
        new PromptShieldChain([
            new PatternPromptShield(new GuardOptions()),
            new ModelPromptShield(new FakeChatClient(), NullLogger<ModelPromptShield>.Instance),
        ]);

    [Theory]
    [MemberData(nameof(All))]
    public async Task RedTeam_entry_has_expected_outcome(Entry entry)
    {
        var shield = Shield();

        var result = entry.IsDocument
            ? await shield.CheckDocumentAsync(entry.Text, Ct)
            : await shield.CheckQuestionAsync(entry.Text, Ct);

        result.Blocked.ShouldBe(entry.Expected == "blocked", $"{entry.Kind} was {(result.Blocked ? "blocked (" + result.Reason + ")" : "allowed")}");
        if (result.Blocked)
        {
            result.Reason.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void The_set_covers_every_kind_with_enough_entries()
    {
        Entries.Count(e => !e.IsDocument).ShouldBeGreaterThanOrEqualTo(40);
        Entries.Count(e => e.Kind == "harmless" && e.Expected == "allowed").ShouldBeGreaterThanOrEqualTo(10);
        Entries.Count(e => e.Kind == "poisoned_document" && e.Expected == "blocked").ShouldBeGreaterThanOrEqualTo(5);
        string[] kinds =
        [
            "direct_injection", "jailbreak", "prompt_leak", "base64", "fake_tag", "cross_tenant", "secrets", "off_topic",
            "hidden_characters", "invisible_characters", "homoglyph", "spaced_letters", "leetspeak",
        ];
        kinds.ShouldAllBe(k => Entries.Any(e => e.Kind == k && e.Expected == "blocked"));
    }

    [Fact]
    public async Task Patterns_alone_block_every_attack_but_off_topic()
    {
        // With Assistant:Guard:Classifier=None only the patterns run: every attack except plain off-topic questions is still blocked.
        var patterns = new PatternPromptShield(new GuardOptions());

        foreach (var entry in Entries.Where(e => e.Expected == "blocked" && e.Kind != "off_topic"))
        {
            var result = entry.IsDocument ? await patterns.CheckDocumentAsync(entry.Text, Ct) : await patterns.CheckQuestionAsync(entry.Text, Ct);
            result.Blocked.ShouldBeTrue(entry.ToString());
        }
    }

    [Fact]
    public async Task Question_over_the_length_limit_is_blocked()
    {
        var result = await Shield().CheckQuestionAsync("Who approves laptops? " + new string('x', 2_000), Ct);

        result.ShouldBe(PromptShieldResult.Block(GuardReasons.TooLong));
    }

    [Fact]
    public async Task Seed_policy_passes_the_document_guard()
    {
        var policy = System.Text.Encoding.UTF8.GetString(await DemoSeeder.ReadPolicyAsync(Ct));

        (await Shield().CheckDocumentAsync(policy, Ct)).ShouldBe(PromptShieldResult.Allowed);
    }
}
