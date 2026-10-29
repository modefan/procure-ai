using ProcureAi.Assistant.Domain;
using Shouldly;
using static ProcureAi.Assistant.IntegrationTests.PolicyDocumentsApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// The vector arm's plan on a table of realistic size: 2 000 child chunks of one tenant plus 2 000 of another, statistics up to date
/// (<c>ANALYZE</c>). The arm must be an index scan in distance order on the partial HNSW index, not a sequential scan and a sort of
/// every chunk.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class VectorIndexTests(AssistantApp app)
{
    private const int ChunksPerTenant = 2_000;

    private static readonly string[] Words =
    [
        "laptop", "chair", "travel", "gift", "software", "licence", "quote", "invoice", "supplier", "budget", "approval", "finance",
        "catalogue", "desk", "monitor", "printer", "cleaning", "catering", "training", "consulting", "courier", "fuel", "vehicle", "phone",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Vector_arm_is_an_index_scan_on_the_hnsw_index()
    {
        var tenant = AssistantApp.NewTenant();
        await SeedAsync(tenant);
        await SeedAsync(AssistantApp.NewTenant());
        await app.ScalarAsync<object>("ANALYZE chunks");
        await app.ScalarAsync<object>("ANALYZE policy_documents");

        await using var db = TenantDb(app, tenant);
        var search = SearchSeed.Search(app, db);
        var types = new HashSet<SourceType> { SourceType.Policy, SourceType.Purchase };
        var plan = System.Text.RegularExpressions.Regex.Replace(
            await search.ExplainNearestAsync("laptop docking station approval", types, 20, analyze: true, Ct),
            @"'\[[^\]]*\]'::vector", "'[1536 floats]'::vector");
        TestContext.Current.SendDiagnosticMessage("Vector arm plan:\n" + plan);

        plan.ShouldContain("Index Scan using ix_chunks_embedding_hnsw on chunks");
        plan.ShouldNotContain("Seq Scan on chunks");
        plan.ShouldNotContain("Sort");
        (await search.NearestArmAsync("laptop docking station approval", types, 20, Ct)).Count.ShouldBe(20);
    }

    private async Task SeedAsync(string tenant)
    {
        var random = new Random(tenant.GetHashCode());
        var texts = Enumerable.Range(1, ChunksPerTenant)
            .Select(i => $"Rule {i}: " + string.Join(' ', Enumerable.Range(0, 12).Select(_ => Words[random.Next(Words.Length)])) + ".")
            .ToArray();
        await SearchSeed.AddDocumentAsync(app, tenant, "Large policy", 1, [(1, new SeedSection(["1 Rules"], "All the rules.", texts))]);
    }
}
