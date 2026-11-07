using System.Globalization;
using System.Numerics.Tensors;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Ai;
using ProcureAi.Assistant.Infrastructure.Ingestion;
using ProcureAi.Assistant.Infrastructure.Persistence;
using Shouldly;

namespace ProcureAi.Assistant.Evaluation;

/// <summary>
/// Groundedness and relevance of real answers (plan 05 Task 10), judged by the model with Microsoft.Extensions.AI.Evaluation.Quality.
/// For every question of <c>eval/questions.json</c>: the seed policy is chunked as ingestion chunks it, embedded with the configured
/// embedding model, the six nearest sections are put into the Assistant's own answer prompt (<see cref="PromptBuilder"/>), and the
/// configured chat model answers. <see cref="GroundednessEvaluator"/> (is every claim supported by the sources?) and
/// <see cref="RelevanceEvaluator"/> (does it answer the question?) score the answer from 1 to 5. The report (every question, both scores,
/// the answer) is written to <c>reports/</c> next to this project.
/// <para>
/// Spends tokens, so it only runs with a real provider: set <c>AI__Provider</c> (OpenAI), <c>AI__Endpoint</c>, <c>AI__ApiKey</c> and the
/// model names in the environment or this project's user secrets; under the Fake provider every test is skipped. Retrieval here is
/// vector-only and in memory; the service's hybrid search is measured separately (hit@6, ProcureAi.Assistant.IntegrationTests).
/// </para>
/// </summary>
public sealed class AnswerQualityEvaluation
{
    /// <summary>The average scores (1 to 5) the answers must reach.</summary>
    private const double MinimumAverageGroundedness = 4.0;

    private const double MinimumAverageRelevance = 4.0;

    private const int TopK = 6;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record EvalQuestion(string Question, string[] ExpectedSources, string Tenant);

    private sealed record Section(IReadOnlyList<string> Path, string ParentText, string ChildText, ReadOnlyMemory<float> Vector);

    private sealed record Scored(string Question, string Tenant, string[] ExpectedSources, string[] Retrieved, string Answer, double? Groundedness, double? Relevance);

    [Fact]
    public async Task Answers_are_grounded_and_relevant()
    {
        var configuration = Configuration();
        var provider = configuration["AI:Provider"];
        if (string.IsNullOrWhiteSpace(provider) || string.Equals(provider, nameof(AiProvider.Fake), StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("The evaluation needs a real model (it spends tokens): set AI__Provider, AI__Endpoint, AI__ApiKey. Skipped under the Fake provider.");
        }

        using var tokens = new TokenMeter();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAssistantAi(configuration);
        await using var sp = services.BuildServiceProvider();
        var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        var chat = sp.GetRequiredKeyedService<IChatClient>(AiClients.Utility);
        var embeddings = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var judge = new ChatConfiguration(chat);

        var questions = JsonSerializer.Deserialize<EvalQuestion[]>(
            await File.ReadAllTextAsync(Path.Combine("eval", "questions.json"), Ct), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var sections = await IndexSeedPolicyAsync(embeddings, sp.GetRequiredService<IOptions<Application.AssistantOptions>>().Value.Chunking);

        var results = new List<Scored>();
        foreach (var question in questions)
        {
            var hits = await NearestAsync(embeddings, sections, question.Question);
            var sources = hits.Select((h, i) => new SourceRef($"S{i + 1}", h.ChunkId, SourceType.Policy, h.Title, h.SectionPath, null)).ToList();
            var messages = PromptBuilder.Build(hits, sources, [], question.Question);
            var response = await chat.GetResponseAsync(messages, cancellationToken: Ct);

            var grounding = string.Join("\n\n", hits.Select(h => h.ParentText));
            var groundedness = await new GroundednessEvaluator().EvaluateAsync(
                messages, response, judge, [new GroundednessEvaluatorContext(grounding)], Ct);
            var relevance = await new RelevanceEvaluator().EvaluateAsync(messages, response, judge, cancellationToken: Ct);
            results.Add(new Scored(
                question.Question, question.Tenant, question.ExpectedSources, [.. hits.Select(h => h.SectionPath.LastOrDefault() ?? "")],
                response.Text,
                groundedness.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName).Value,
                relevance.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName).Value));
        }

        var averageGroundedness = results.Average(r => r.Groundedness ?? 0);
        var averageRelevance = results.Average(r => r.Relevance ?? 0);
        var report = await WriteReportAsync(ai, results, averageGroundedness, averageRelevance, tokens.Snapshot());
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"groundedness {averageGroundedness:0.00}, relevance {averageRelevance:0.00}; {tokens.Snapshot()}; report: {report}"));

        averageGroundedness.ShouldBeGreaterThanOrEqualTo(MinimumAverageGroundedness, $"see {report}");
        averageRelevance.ShouldBeGreaterThanOrEqualTo(MinimumAverageRelevance, $"see {report}");
    }

    /// <summary>The Assistant's shipped settings, overridden by this project's user secrets and the environment.</summary>
    internal static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "assistant.appsettings.json"), optional: false)
            .AddUserSecrets<AnswerQualityEvaluation>(optional: true)
            .AddEnvironmentVariables()
            .Build();

    /// <summary>The seed policy, extracted and chunked as ingestion does, each child embedded with the configured model.</summary>
    private static async Task<IReadOnlyList<Section>> IndexSeedPolicyAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddings, ChunkingOptions chunking)
    {
        using var content = new MemoryStream(await DemoSeeder.ReadPolicyAsync(Ct));
        var planned = new ChunkPlanner(chunking).Plan(new MarkdownTextExtractor().Extract(content));
        var parents = planned.Where(c => c.Kind == ChunkKind.Parent).ToDictionary(c => c.Ordinal);
        var children = planned.Where(c => c.Kind == ChunkKind.Child).ToList();
        var vectors = await embeddings.GenerateAsync(children.Select(c => c.Text), cancellationToken: Ct);
        return [.. children.Select((c, i) => new Section(c.SectionPath, parents[c.ParentOrdinal!.Value].Text, c.Text, vectors[i].Vector))];
    }

    private static async Task<IReadOnlyList<SearchHit>> NearestAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddings, IReadOnlyList<Section> sections, string question)
    {
        var query = (await embeddings.GenerateAsync([question], cancellationToken: Ct))[0].Vector;
        return
        [
            .. sections
                .Select(s => (Section: s, Score: (double)TensorPrimitives.CosineSimilarity(query.Span, s.Vector.Span)))
                .OrderByDescending(x => x.Score)
                .Take(TopK)
                .Select(x => new SearchHit(
                    Guid.NewGuid(), SourceType.Policy, DemoSeeder.Title, x.Section.Path, x.Section.ChildText, x.Section.ParentText, null, x.Score)),
        ];
    }

    private static async Task<string> WriteReportAsync(
        AiOptions ai, IReadOnlyList<Scored> results, double groundedness, double relevance, TokenTotals tokens)
    {
        var directory = Path.Combine(ProjectDirectory(), "reports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"evaluation-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        var report = new
        {
            createdUtc = DateTime.UtcNow,
            chatModel = ai.EffectiveChatModel,
            embeddingModel = ai.EffectiveEmbeddingModel,
            questions = results.Count,
            averageGroundedness = Math.Round(groundedness, 2),
            averageRelevance = Math.Round(relevance, 2),
            tokens,
            results,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), Encoding.UTF8, Ct);
        return path;
    }

    /// <summary>This project's folder (the report is kept there, not in bin/), found from the output folder upwards.</summary>
    internal static string ProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProcureAi.Assistant.Evaluation.csproj")))
            {
                return directory.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }
}
