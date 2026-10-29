using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Ai.Fake;
using Shouldly;
using static ProcureAi.Assistant.IntegrationTests.PolicyDocumentsApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// Hybrid retrieval (plan 05 Task 3): the vector and full-text arms, reciprocal rank fusion, the searchable-chunk filters (ledger ruling B)
/// and tenant isolation (Review Focus 4). The chunks are written straight to the database with Fake embeddings (<see cref="SearchSeed"/>);
/// the Fake embedding is a hashed bag of words and bigrams, so "semantic" here means "shares words", while the full-text arm needs every
/// stemmed term of the query (<c>websearch_to_tsquery</c> is an AND).
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class HybridSearchTests(AssistantApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Marker(string name) => $"{name}{Guid.NewGuid():N}"[..(name.Length + 10)];

    private static SearchRequest Policy(string query, int topK = 6) => new(query, new HashSet<SourceType> { SourceType.Policy }, topK);

    [Fact]
    public async Task Keyword_only_match_found_by_full_text_arm()
    {
        var tenant = AssistantApp.NewTenant();
        // A long chunk that has the phrase once (a weak vector match), and four short ones that share one of its words each (strong vector
        // matches that the full-text arm rejects, since they lack the other term).
        const string target =
            "Accounts payable checks every supplier invoice before it is paid. The purchase order, the delivery note and the invoice must"
            + " agree on quantities and prices, the bank details must already be on file and verified by a second person, the invoice date"
            + " must fall in the current financial month, credit notes are matched to the original invoice, foreign currency invoices are"
            + " converted at the rate of the invoice date, and the document must show the supplier's registered VAT number in full.";
        var seeded = await SearchSeed.AddTextsAsync(app, tenant, target, "VAT rates.", "VAT applies.", "Order number.", "Number pages.");
        var targetId = seeded.Children[target];
        const string query = "VAT number";

        // The arms on their own, with three candidates each: only the full-text arm has the target.
        (await SearchSeed.NearestAsync(app, tenant, query, 3)).ShouldNotContain(targetId);
        (await SearchSeed.FullTextMatchesAsync(app, tenant, query)).ShouldBe([targetId]);

        await using var db = TenantDb(app, tenant);
        var hits = await SearchSeed.Search(app, db, candidatesPerArm: 3, topK: 3).SearchAsync(Policy(query, 3), Ct);

        hits.Select(h => h.ChunkId).ShouldContain(targetId);
        // Rank 1 of the full-text arm ties with rank 1 of the vector arm (1/61 each): both lead the list.
        hits.Take(2).Select(h => h.ChunkId).ShouldContain(targetId);
    }

    [Fact]
    public async Task Semantic_match_found_by_vector_arm()
    {
        var tenant = AssistantApp.NewTenant();
        const string target = "Every laptop comes with a docking station from the IT catalogue.";
        var seeded = await SearchSeed.AddTextsAsync(
            app, tenant, "Office chairs are ordered through facilities.", target, "Travel is booked by the travel desk.");
        var targetId = seeded.Children[target];
        // "zebra" is in no chunk, so the full-text arm (every term required) finds nothing; the shared words still make the target the
        // nearest vector.
        const string query = "laptop docking station zebra";
        (await SearchSeed.FullTextMatchesAsync(app, tenant, query)).ShouldBeEmpty();

        await using var db = TenantDb(app, tenant);
        var hits = await SearchSeed.Search(app, db).SearchAsync(Policy(query), Ct);

        hits.ShouldNotBeEmpty();
        hits[0].ChunkId.ShouldBe(targetId);
    }

    [Fact]
    public async Task Search_never_returns_other_tenant_chunks()
    {
        var acme = AssistantApp.NewTenant();
        var globex = AssistantApp.NewTenant();
        var marker = Marker("gifts");
        var text = $"Gift registers of {marker} are kept by finance.";
        var own = await SearchSeed.AddTextsAsync(app, acme, text);
        // Globex has the very same text (the nearest vector and the best keyword match), more of it, and a purchase chunk too.
        await SearchSeed.AddTextsAsync(app, globex, text, $"{text} Twice.", $"Also {marker}.");
        await using (var seed = TenantDb(app, globex))
        {
            var chunk = new Chunk(
                Guid.CreateVersion7(), globex, SourceType.Purchase, ChunkKind.Child, null, 0, "", $"PR-0001 · {marker} gifts", new string('0', 64),
                DateTimeOffset.UtcNow, purchaseRequestId: Guid.NewGuid());
            chunk.SetEmbedding(AiOptions.FakeEmbeddingModel, FakeEmbeddingGenerator.Embed(chunk.Text), null);
            seed.Chunks.Add(chunk);
            await seed.SaveChangesAsync(Ct);
        }

        await using var db = TenantDb(app, acme);
        var hits = await SearchSeed.Search(app, db).SearchAsync(new SearchRequest(marker, SearchRequest.AllTypes, 20), Ct);

        hits.Select(h => h.ChunkId).ShouldBe([own.Children[text]]);

        // A caller without a tenant finds nothing at all.
        await using var noTenant = TenantDb(app, "");
        (await SearchSeed.Search(app, noTenant).SearchAsync(new SearchRequest(marker, SearchRequest.AllTypes, 20), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_ignores_removed_and_old_versions()
    {
        var tenant = AssistantApp.NewTenant();
        var m = Marker("ver");
        SeedSection Section(string word) => new(["1 Rule"], $"Parent {word} {m} rule.", $"{word} {m} rule.");

        // Replaced: version 2 is current, version 1's chunks are left over.
        var replaced = await SearchSeed.AddDocumentAsync(app, tenant, "Replaced", 2, [(1, Section("old")), (2, Section("new"))]);
        // Removed (soft delete) while version 1 was current.
        await SearchSeed.AddDocumentAsync(app, tenant, "Removed", 1, [(1, Section("gone"))], removed: true);
        // The first version is still being embedded: nothing is current.
        await SearchSeed.AddDocumentAsync(app, tenant, "Pending", 0, [(1, Section("pending"))]);
        // Version 2 is being embedded: version 1 stays the searchable one.
        var replacing = await SearchSeed.AddDocumentAsync(app, tenant, "Replacing", 1, [(1, Section("current")), (2, Section("next"))], newestVersion: 2);

        await using var db = TenantDb(app, tenant);
        var hits = await SearchSeed.Search(app, db).SearchAsync(Policy(m, 20), Ct);

        hits.Select(h => h.ChildText).ShouldBe([$"new {m} rule.", $"current {m} rule."], ignoreOrder: true);
        hits.Select(h => h.ChunkId).ShouldBe([replaced.Children[$"new {m} rule."], replacing.Children[$"current {m} rule."]], ignoreOrder: true);
        // Parents carry the marker too, but only children are searched.
        hits.Select(h => h.ChunkId).ShouldNotContain(id => replaced.Parents.Values.Contains(id) || replacing.Parents.Values.Contains(id));
    }

    [Fact]
    public async Task Hits_carry_parent_text_and_section_path()
    {
        var tenant = AssistantApp.NewTenant();
        var m = Marker("thresholds");
        var parentText = $"3.2 Thresholds. Purchases above R 200 000 need finance approval {m}. Below that, the manager approves.";
        var child = $"Purchases above R 200 000 need finance approval {m}.";
        var seeded = await SearchSeed.AddDocumentAsync(
            app, tenant, "Acme procurement policy", 1, [(1, new SeedSection(["3 Approvals", "3.2 Thresholds"], parentText, child))]);

        await using var db = TenantDb(app, tenant);
        var hit = (await SearchSeed.Search(app, db).SearchAsync(Policy($"finance approval {m}"), Ct)).ShouldHaveSingleItem();

        hit.ChunkId.ShouldBe(seeded.Children[child]);
        hit.Type.ShouldBe(SourceType.Policy);
        hit.Title.ShouldBe("Acme procurement policy");
        hit.SectionPath.ShouldBe(["3 Approvals", "3.2 Thresholds"]);
        hit.ChildText.ShouldBe(child);
        hit.ParentText.ShouldBe(parentText);
        hit.Reference.ShouldBeNull();
        hit.Score.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("\"unclosed quote")]
    [InlineData("-")]
    [InlineData("OR")]
    [InlineData("a & b | !c")]
    [InlineData("<-> :* \\ ' ((")]
    [InlineData("'; DROP TABLE chunks; --")]
    [InlineData("the of and")]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Odd_queries_never_throw(string query)
    {
        var tenant = AssistantApp.NewTenant();
        await SearchSeed.AddTextsAsync(app, tenant, "Gifts above R 500 are declared.");

        await using var db = TenantDb(app, tenant);
        var hits = await SearchSeed.Search(app, db).SearchAsync(Policy(query), Ct);

        hits.Count.ShouldBeLessThanOrEqualTo(1);
        if (string.IsNullOrWhiteSpace(query))
        {
            hits.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task TopK_limits_the_hits_and_types_filter_them()
    {
        var tenant = AssistantApp.NewTenant();
        var m = Marker("many");
        await SearchSeed.AddTextsAsync(app, tenant, [.. Enumerable.Range(1, 10).Select(i => $"Rule {i} about {m}.")]);

        await using var db = TenantDb(app, tenant);
        var search = SearchSeed.Search(app, db);
        (await search.SearchAsync(Policy(m, 4), Ct)).Count.ShouldBe(4);
        (await search.SearchAsync(new SearchRequest(m, new HashSet<SourceType> { SourceType.Purchase }, 4), Ct)).ShouldBeEmpty();
        // 0 takes Assistant:Retrieval:TopK (6).
        (await search.SearchAsync(Policy(m, 0), Ct)).Count.ShouldBe(6);
    }
}
