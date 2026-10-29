using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Info;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Ai.Fake;
using ProcureAi.Assistant.Infrastructure.Persistence;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Persistence;
using Shouldly;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// The Assistant's skeleton (plan 05 Task 1) on real infrastructure: the migration (pgvector, the HNSW and GIN indexes, CDC, Wolverine's
/// message store), readiness of both databases, the info endpoint on the Fake provider, and the model calls' telemetry.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class AssistantServiceTests(AssistantApp app)
{
    private const string Info = "/api/v1/assistant/info";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_creates_vector_extension_and_hnsw_index()
    {
        (await app.ScalarAsync<string>("SELECT extname::text FROM pg_extension WHERE extname = 'vector'")).ShouldBe("vector");

        var hnsw = await app.ScalarAsync<string>("SELECT indexdef FROM pg_indexes WHERE tablename = 'chunks' AND indexname = 'ix_chunks_embedding_hnsw'");
        hnsw.ShouldNotBeNull().ShouldContain("USING hnsw (embedding vector_cosine_ops)");
        var gin = await app.ScalarAsync<string>("SELECT indexdef FROM pg_indexes WHERE tablename = 'chunks' AND indexname = 'ix_chunks_search_gin'");
        gin.ShouldNotBeNull().ShouldContain("USING gin (search)");

        (await app.ScalarAsync<string>(
                "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'chunks'::regclass AND attname = 'embedding'"))
            .ShouldBe("vector(1536)");
        // A stored generated column: Postgres keeps the full-text vector of context and text itself.
        (await app.ScalarAsync<string>(
                "SELECT pg_get_expr(adbin, adrelid) FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum " +
                "WHERE d.adrelid = 'chunks'::regclass AND a.attname = 'search' AND a.attgenerated = 's'"))
            .ShouldNotBeNull().ShouldContain("to_tsvector('english'");

        foreach (var table in new[] { "policy_documents", "policy_document_files", "chunks", "purchases", "audit_entries" })
        {
            (await app.ScalarAsync<long>($"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'"))
                .ShouldBe(1L, table);
        }

        // Debezium's publication has the documents and the audit rows only (never chunks, file bytes or Wolverine's tables).
        (await app.ColumnAsync("SELECT tablename::text FROM pg_publication_tables WHERE pubname = 'dbz_publication' ORDER BY tablename"))
            .ShouldBe(["audit_entries", "policy_documents"]);
        // Wolverine's outbox and inbox, created by --migrate (the API never changes the schema at startup in production).
        (await app.ScalarAsync<long>(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'wolverine' AND table_name = 'wolverine_incoming_envelopes'"))
            .ShouldBe(1L);
    }

    [Fact]
    public async Task Chunk_embeddings_round_trip_and_rank_by_cosine_distance()
    {
        var tenant = AssistantApp.NewTenant();
        await using var db = NewDbContext();
        var document = new PolicyDocument(Guid.CreateVersion7(), tenant, "Procurement policy", "policy.md", "admin", DateTimeOffset.UtcNow);
        db.PolicyDocuments.Add(document);
        var texts = new[]
        {
            "Purchases above R 200 000 need finance approval.",
            "Gifts from suppliers above R 500 must be declared.",
            "Software over R 100 000 needs two quotes.",
        };
        var chunks = texts.Select((text, i) =>
        {
            var chunk = new Chunk(Guid.CreateVersion7(), tenant, SourceType.Policy, ChunkKind.Child, null, i, "1 Rules", text, $"hash{i}",
                DateTimeOffset.UtcNow, document.Id, 1);
            chunk.SetEmbedding(AiOptions.FakeEmbeddingModel, FakeEmbeddingGenerator.Embed(text), null);
            return chunk;
        }).ToList();
        db.Chunks.AddRange(chunks);
        await db.SaveChangesAsync(Ct);

        var stored = await db.Chunks.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == chunks[0].Id, Ct);
        stored.Embedding.ShouldBe(chunks[0].Embedding);
        stored.TokenCount.ShouldBe((int)Math.Ceiling(texts[0].Length / 4.0));

        var question = new Pgvector.Vector(FakeEmbeddingGenerator.Embed("Who must approve purchases above R 200 000?"));
        var nearest = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM chunks WHERE tenant_id = {tenant} ORDER BY embedding <=> {question} LIMIT 1")
            .ToListAsync(Ct);
        nearest.ShouldBe([chunks[0].Id]);
        var keyword = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM chunks WHERE tenant_id = {tenant} AND search @@ plainto_tsquery('english', 'gifts')")
            .ToListAsync(Ct);
        keyword.ShouldBe([chunks[1].Id]);
    }

    [Fact]
    public async Task Chunks_and_files_are_not_audited_but_documents_are()
    {
        var tenant = AssistantApp.NewTenant();
        await using var db = NewDbContext();
        var document = new PolicyDocument(Guid.CreateVersion7(), tenant, "Policy", "policy.md", "admin", DateTimeOffset.UtcNow);
        db.PolicyDocuments.Add(document);
        db.PolicyDocumentFiles.Add(new PolicyDocumentFile(
            document.Id, 1, tenant, "text/markdown", "# Policy"u8.ToArray(), new string('0', 64), DateTimeOffset.UtcNow));
        var chunk = new Chunk(
            Guid.CreateVersion7(), tenant, SourceType.Policy, ChunkKind.Parent, null, 0, "Policy", "# Policy", "h", DateTimeOffset.UtcNow, document.Id, 1);
        chunk.SetEmbedding(AiOptions.FakeEmbeddingModel, FakeEmbeddingGenerator.Embed("Policy"), "context");
        db.Chunks.Add(chunk);
        var purchase = new Purchase(Guid.NewGuid(), tenant, "PR-0001", "Laptops", "IT-200", Money.Zar(140_000m), null, "2 x Laptop", DateTimeOffset.UtcNow);
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync(Ct);

        // No caller here: the rows carry no tenant, so they are found by the keys this test wrote.
        var keys = new[] { document.Id.ToString(), chunk.Id.ToString(), purchase.RequestId.ToString() };
        var audited = await db.Set<AuditEntry>().AsNoTracking()
            .Where(e => keys.Contains(e.PrimaryKey) || e.PrimaryKey.StartsWith(document.Id.ToString()))
            .Select(e => e.Table).ToListAsync(Ct);
        audited.ShouldBe(["policy_documents", "purchases"], ignoreOrder: true);
    }

    [Fact]
    public async Task Info_reports_fake_provider()
    {
        using var response = await app.Client.SendAsync(AssistantApp.Get(Info, AssistantApp.Token(AssistantApp.NewTenant())), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var info = (await response.Content.ReadFromJsonAsync<AssistantInfo>(Ct))!;
        info.ShouldBe(new AssistantInfo("Fake", AiOptions.FakeChatModel, DemoModel: true, QuestionsPerDay: 50));
    }

    [Fact]
    public async Task Info_without_assistant_use_is_403()
    {
        var tenant = AssistantApp.NewTenant();
        app.Access.SetMatrix(tenant, new Dictionary<string, string[]> { ["requester"] = [Permissions.RequestsRead, Permissions.RequestsCreate] });

        using var forbidden = await app.Client.SendAsync(AssistantApp.Get(Info, AssistantApp.Token(tenant)), Ct);
        using var anonymous = await app.Client.SendAsync(AssistantApp.Get(Info, null), Ct);

        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Readiness_checks_postgres_and_mongo()
    {
        var checks = app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(r => r.Tags.Contains("ready")).Select(r => r.Name).ToList();
        using var response = await app.Client.GetAsync("/health/ready", Ct);

        checks.ShouldBe(["postgres", "mongodb", "rabbitmq", "redis"], ignoreOrder: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Model_calls_are_traced_on_the_assistant_source_without_content()
    {
        var answer = app.Services.GetRequiredKeyedService<IChatClient>(AiClients.Answer);
        var embeddings = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var marker = Guid.NewGuid().ToString("N");

        await answer.GetResponseAsync(
            [new(ChatRole.System, AiTasks.Line(AiTasks.Answer)), new(ChatRole.User, $"[S1] The {marker} rule.\nsecret question {marker}")],
            cancellationToken: Ct);
        await embeddings.GenerateAsync([$"text {marker}"], cancellationToken: Ct);
        app.Services.GetRequiredService<TracerProvider>().ForceFlush().ShouldBeTrue();

        Activity[] seen;
        lock (app.ExportedActivities)
        {
            seen = [.. app.ExportedActivities.Where(a => a.Source.Name == AiClients.TelemetrySource)];
        }

        // Exported by the service's own tracer provider: the service defaults trace every ProcureAi.* source (AiSetup registers none).
        seen.ShouldContain(a => a.OperationName.StartsWith("chat", StringComparison.Ordinal));
        seen.ShouldContain(a => a.OperationName.StartsWith("embeddings", StringComparison.Ordinal));
        // LogContent is false outside Development: no prompt or answer text in any tag or event.
        foreach (var activity in seen)
        {
            activity.TagObjects.ShouldNotContain(t => (t.Value as string ?? "").Contains(marker), activity.OperationName);
            activity.Events.SelectMany(e => e.Tags).ShouldNotContain(t => (t.Value as string ?? "").Contains(marker), activity.OperationName);
        }
    }

    private AssistantDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AssistantDbContext>()
            .UseNpgsql(app.Infra.DbConnectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(new AuditSaveChangesInterceptor(TimeProvider.System))
            .Options);
}
