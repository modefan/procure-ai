using System.Globalization;
using System.Text.Json;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Persistence;
using Shouldly;
using static ProcureAi.Assistant.IntegrationTests.PolicyDocumentsApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// The retrieval half of the evaluation (plan 05 Task 10): every question of the evaluation set (<c>eval/questions.json</c> of
/// ProcureAi.Assistant.Evaluation) is searched in its tenant's seeded procurement policy, as the answer flow searches (hybrid, top 6), and
/// counts as a hit when one of the six hits is under an expected section. This runs on the Fake provider too: its lexical embeddings and the
/// full-text arm must find the right section for at least 80 % of the questions. The model-judged half (groundedness, relevance) needs a real
/// model and lives in the evaluation project.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class RetrievalHitRateTests(AssistantApp app)
{
    private const int TopK = 6;
    private const double MinimumHitRate = 0.8;

    private static readonly IReadOnlySet<SourceType> PolicyOnly = new HashSet<SourceType> { SourceType.Policy };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record EvalQuestion(string Question, string[] ExpectedSources, string Tenant);

    [Fact]
    public async Task Seed_policy_hit_at_6_is_at_least_80_percent()
    {
        var questions = JsonSerializer.Deserialize<EvalQuestion[]>(
            await File.ReadAllTextAsync(Path.Combine("eval", "questions.json"), Ct), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        questions.Length.ShouldBeGreaterThanOrEqualTo(25);

        // The fixture seeded the policy for acme and globex; their ingestion runs in the background.
        foreach (var (tenant, id) in DemoSeeder.Documents)
        {
            await WaitForStatusAsync(app.Client, Admin(tenant), id, "Ready");
        }

        var misses = new List<string>();
        foreach (var question in questions)
        {
            await using var db = TenantDb(app, question.Tenant);
            var hits = await SearchSeed.Search(app, db, topK: TopK).SearchAsync(new SearchRequest(question.Question, PolicyOnly, TopK), Ct);
            hits.Count.ShouldBeLessThanOrEqualTo(TopK);
            if (!hits.Any(h => h.SectionPath.Any(heading => question.ExpectedSources.Contains(heading, StringComparer.OrdinalIgnoreCase))))
            {
                misses.Add($"{question.Question} (expected {string.Join(" or ", question.ExpectedSources)}; got "
                           + string.Join(", ", hits.Select(h => h.SectionPath.LastOrDefault() ?? "?")) + ")");
            }
        }

        var hitRate = (questions.Length - misses.Count) / (double)questions.Length;
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"hit@{TopK} = {hitRate:0.000} ({questions.Length - misses.Count} of {questions.Length}); misses: {(misses.Count == 0 ? "none" : string.Join(" | ", misses))}"));
        hitRate.ShouldBeGreaterThanOrEqualTo(MinimumHitRate, "misses: " + string.Join(" | ", misses));
    }
}
