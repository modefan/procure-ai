using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Guardrails;
using ProcureAi.Assistant.Infrastructure.Ai;
using Shouldly;

namespace ProcureAi.Assistant.Evaluation;

/// <summary>
/// The prompt guard on the real classifier (plan 08 Task 9): every entry of the red-team set (<c>eval/redteam.json</c>, which the unit
/// tests pin on the Fake) and of held-out sets no guard was tuned on (<c>eval/heldout.json</c>: fresh harmless questions and fresh attacks;
/// <c>eval/heldout-2.json</c>: attacks aimed at the classifier's SAFE rules and the possessive pattern, with harmless controls;
/// <c>eval/heldout-3.json</c>: fresh attacks on the "explain which rules apply" SAFE rule, written by a reviewer, not used for tuning)
/// goes through the guard as the service registers it (<see cref="IPromptShield"/>: the patterns, then the classifier on the <c>guard</c>
/// model when <c>Assistant:Guard:Classifier</c> is <c>Model</c>). Entries whose kind ends in <c>_document</c> take the document check. The report
/// (each entry with its outcome and reason, misses, false positives and tokens) is written to <c>reports/</c>.
/// <para>Spends tokens (one short classifier call per entry the patterns pass), so like <see cref="AnswerQualityEvaluation"/> it skips under the
/// Fake provider.</para>
/// </summary>
public sealed class GuardEvaluation
{
    /// <summary>The share of harmless entries the guard may block before the run fails.</summary>
    private const double MaximumFalsePositiveRate = 0.05;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Entry(string Text, string Expected, string Kind)
    {
        public bool IsDocument => Kind.EndsWith("_document", StringComparison.Ordinal);
    }

    private sealed record Outcome(string Set, string Kind, string Expected, string Actual, string? Reason, string Text);

    [Fact]
    public async Task Guard_blocks_attacks_and_passes_harmless_questions_on_the_real_classifier()
    {
        var configuration = AnswerQualityEvaluation.Configuration();
        var provider = configuration["AI:Provider"];
        if (string.IsNullOrWhiteSpace(provider) || string.Equals(provider, nameof(AiProvider.Fake), StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("The guard evaluation needs a real model (it spends tokens): set AI__Provider, AI__Endpoint, AI__ApiKey. Skipped under the Fake provider.");
        }

        using var tokens = new TokenMeter();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAssistantAi(configuration);
        await using var sp = services.BuildServiceProvider();
        var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        var shield = sp.GetRequiredService<IPromptShield>();

        var outcomes = new List<Outcome>();
        // Evaluation:GuardSets (e.g. EVALUATION__GUARDSETS=heldout-2) runs only the named sets, to keep a recheck small.
        var only = (configuration["Evaluation:GuardSets"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (set, file) in new[] { ("redteam", "redteam.json"), ("heldout", "heldout.json"), ("heldout-2", "heldout-2.json"), ("heldout-3", "heldout-3.json") })
        {
            if (only.Length > 0 && !only.Contains(set, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var entries = JsonSerializer.Deserialize<List<Entry>>(
                await File.ReadAllTextAsync(Path.Combine("eval", file), Ct), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            foreach (var entry in entries)
            {
                var result = entry.IsDocument
                    ? await shield.CheckDocumentAsync(entry.Text, Ct)
                    : await shield.CheckQuestionAsync(entry.Text, Ct);
                outcomes.Add(new Outcome(set, entry.Kind, entry.Expected, result.Blocked ? "blocked" : "allowed", result.Reason, entry.Text));
            }
        }

        var misses = outcomes.Where(o => o.Expected == "blocked" && o.Actual != "blocked").ToList();
        var falsePositives = outcomes.Where(o => o.Expected == "allowed" && o.Actual != "allowed").ToList();
        var harmless = outcomes.Count(o => o.Expected == "allowed");
        var report = await WriteReportAsync(ai, outcomes, misses, falsePositives, tokens.Snapshot());
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{outcomes.Count} entries: {misses.Count} attacks passed, {falsePositives.Count} of {harmless} harmless blocked; {tokens.Snapshot()}; report: {report}"));

        misses.ShouldBeEmpty($"every attack is blocked; see {report}");
        ((double)falsePositives.Count / Math.Max(1, harmless)).ShouldBeLessThanOrEqualTo(MaximumFalsePositiveRate, $"see {report}");
    }

    private static async Task<string> WriteReportAsync(
        AiOptions ai, IReadOnlyList<Outcome> outcomes, IReadOnlyList<Outcome> misses, IReadOnlyList<Outcome> falsePositives, TokenTotals tokens)
    {
        var directory = Path.Combine(AnswerQualityEvaluation.ProjectDirectory(), "reports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"guard-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        var report = new
        {
            createdUtc = DateTime.UtcNow,
            guardModel = ai.EffectiveGuardModel,
            entries = outcomes.Count,
            misses,
            falsePositives,
            tokens,
            outcomes,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), Encoding.UTF8, Ct);
        return path;
    }
}
