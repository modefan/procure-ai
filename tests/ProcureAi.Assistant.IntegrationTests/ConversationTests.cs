using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using static ProcureAi.Assistant.IntegrationTests.ChatApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// Conversations (plan 05 Task 5a) in MongoDB: what an answer stores, regenerate and edit, the owner and tenant boundaries (Review Focus 4:
/// another user's or tenant's conversation is a 404 that reveals nothing) and the admin view (<c>admin.system</c>, ruling 18).
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class ConversationTests(AssistantApp app)
{
    private const string Question = "Do purchases above R 100 000 need finance approval before the order is placed?";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Tenant, string Token, Guid Conversation)> SeededConversationAsync()
    {
        var tenant = AssistantApp.NewTenant();
        await SeedPolicyAsync(app, tenant, StreamingTests.ThresholdRule, StreamingTests.LaptopRule);
        var token = User(tenant);
        return (tenant, token, await CreateAsync(app.Client, token));
    }

    [Fact]
    public async Task Finished_message_saved_with_sources_and_usage()
    {
        var (_, token, conversation) = await SeededConversationAsync();

        var result = await AskOkAsync(app.Client, token, conversation, Question);

        var stored = await ConversationAsync(app.Client, token, conversation);
        stored.GetProperty("title").GetString().ShouldBe(Question[..60].TrimEnd());
        var messages = Messages(stored);
        messages.Length.ShouldBe(2);
        messages[0].GetProperty("role").GetString().ShouldBe("User");
        messages[0].GetProperty("text").GetString().ShouldBe(Question);
        var answer = messages[1];
        answer.GetProperty("id").GetGuid().ShouldBe(result.Done.Data.GetProperty("messageId").GetGuid());
        answer.GetProperty("role").GetString().ShouldBe("Assistant");
        answer.GetProperty("status").GetString().ShouldBe("Complete");
        answer.GetProperty("text").GetString().ShouldBe(result.StreamedText);
        answer.GetProperty("model").GetString().ShouldBe("fake-chat");
        answer.GetProperty("usage").GetProperty("input").GetInt64().ShouldBe(result.Done.Data.GetProperty("usage").GetProperty("input").GetInt64());
        answer.GetProperty("usage").GetProperty("output").GetInt64().ShouldBeGreaterThan(0);
        var sources = answer.GetProperty("sources").EnumerateArray().ToList();
        sources.Select(s => s.GetProperty("ref").GetString()).ShouldBe(["S1", "S2"]);
        sources[0].GetProperty("title").GetString().ShouldBe("Procurement policy");
        answer.GetProperty("toolSteps").GetArrayLength().ShouldBe(0);

        var list = await GetOkAsync(app.Client, Conversations, token);
        var item = list.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == conversation);
        item.GetProperty("title").GetString().ShouldBe(Question[..60].TrimEnd());
        item.GetProperty("updatedAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Regenerate_replaces_last_answer()
    {
        var (_, token, conversation) = await SeededConversationAsync();
        var first = await AskOkAsync(app.Client, token, conversation, Question);

        var again = await AskOkAsync(app.Client, token, conversation, Question, "regenerate");

        var messages = Messages(await ConversationAsync(app.Client, token, conversation));
        messages.Length.ShouldBe(2);
        messages[0].GetProperty("text").GetString().ShouldBe(Question);
        var answerId = messages[1].GetProperty("id").GetGuid();
        answerId.ShouldBe(again.Done.Data.GetProperty("messageId").GetGuid());
        answerId.ShouldNotBe(first.Done.Data.GetProperty("messageId").GetGuid());
    }

    [Fact]
    public async Task Edit_replaces_last_question_and_answer()
    {
        var (_, token, conversation) = await SeededConversationAsync();
        await AskOkAsync(app.Client, token, conversation, Question);
        await AskOkAsync(app.Client, token, conversation, "How are laptops bought?");

        await AskOkAsync(app.Client, token, conversation, "How is computer equipment bought?", "edit");

        var messages = Messages(await ConversationAsync(app.Client, token, conversation));
        messages.Select(m => m.GetProperty("role").GetString()).ShouldBe(["User", "Assistant", "User", "Assistant"]);
        messages[0].GetProperty("text").GetString().ShouldBe(Question);
        messages[2].GetProperty("text").GetString().ShouldBe("How is computer equipment bought?");
        messages[3].GetProperty("status").GetString().ShouldBe("Complete");
    }

    [Fact]
    public async Task Other_tenant_conversation_is_404()
    {
        var (_, token, conversation) = await SeededConversationAsync();
        await AskOkAsync(app.Client, token, conversation, Question);
        var otherTenant = AssistantApp.NewTenant();
        var sameUserElsewhere = User(otherTenant);

        await ExpectHiddenAsync(sameUserElsewhere, conversation);
        // An admin of the other tenant cannot see it either.
        using var admin = await SendAsync(app.Client, HttpMethod.Get, $"{AdminConversations}/{conversation}", Admin(otherTenant));
        admin.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var key = ChatApi.AdminKeyRequest(HttpMethod.Get, $"{AdminConversations}/{conversation}", otherTenant);
        (await app.Client.SendAsync(key, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Still there for its owner.
        Messages(await ConversationAsync(app.Client, token, conversation)).Length.ShouldBe(2);
    }

    [Fact]
    public async Task Other_users_conversation_is_404()
    {
        var (tenant, token, conversation) = await SeededConversationAsync();
        await AskOkAsync(app.Client, token, conversation, Question);
        var colleague = User(tenant, "sam");

        await ExpectHiddenAsync(colleague, conversation);
        (await GetOkAsync(app.Client, Conversations, colleague)).EnumerateArray().ShouldNotContain(c => c.GetProperty("id").GetGuid() == conversation);

        Messages(await ConversationAsync(app.Client, token, conversation)).Length.ShouldBe(2);
    }

    [Fact]
    public async Task Admin_key_acts_for_the_on_behalf_of_user()
    {
        var tenant = AssistantApp.NewTenant();
        using var create = AdminKeyRequest(HttpMethod.Post, Conversations, tenant, onBehalfOf: "rita", body: new { });
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var created = await app.Client.SendAsync(create, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        // Rita owns it; the key on behalf of someone else does not see it.
        (await ConversationAsync(app.Client, User(tenant), id)).GetProperty("id").GetGuid().ShouldBe(id);
        using var other = AdminKeyRequest(HttpMethod.Get, $"{Conversations}/{id}", tenant, onBehalfOf: "sam");
        (await app.Client.SendAsync(other, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Every write of a conversation carries the user's <c>_audit</c> stamp (plan 06 ruling 16), so change data capture attributes it to the
    /// app; a delete stamps the document "deleting" first.
    /// </summary>
    [Fact]
    public async Task Conversation_writes_carry_the_users_audit_stamp()
    {
        var tenant = AssistantApp.NewTenant();
        await SeedPolicyAsync(app, tenant, StreamingTests.ThresholdRule, StreamingTests.LaptopRule);
        var token = User(tenant, "rita");
        var conversation = await CreateAsync(app.Client, token);
        var collection = app.Infra.Mongo.Client.GetDatabase(AssistantInfraFixture.ChatDatabase)
            .GetCollection<BsonDocument>("conversations");
        var key = Builders<BsonDocument>.Filter.Eq("_id", conversation.ToString("D"));
        var created = await collection.Find(key).SingleAsync(Ct);

        await AskOkAsync(app.Client, token, conversation, Question);
        var answered = await collection.Find(key).SingleAsync(Ct);

        foreach (var document in new[] { created, answered })
        {
            var stamp = document["_audit"].AsBsonDocument;
            stamp["userId"].AsString.ShouldBe("rita");
            stamp.Contains("deleting").ShouldBeFalse();
        }

        answered["_audit"]["at"].ToUniversalTime().ShouldBeGreaterThanOrEqualTo(created["_audit"]["at"].ToUniversalTime());
    }

    [Fact]
    public async Task Deleted_conversation_is_gone()
    {
        var (_, token, conversation) = await SeededConversationAsync();
        await AskOkAsync(app.Client, token, conversation, Question);

        using var deleted = await SendAsync(app.Client, HttpMethod.Delete, $"{Conversations}/{conversation}", token);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var get = await SendAsync(app.Client, HttpMethod.Get, $"{Conversations}/{conversation}", token);
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetOkAsync(app.Client, Conversations, token)).EnumerateArray().ShouldNotContain(c => c.GetProperty("id").GetGuid() == conversation);
    }

    [Fact]
    public async Task Admin_reads_full_record_with_admin_system_only()
    {
        var (tenant, token, conversation) = await SeededConversationAsync();
        await AskOkAsync(app.Client, token, conversation, Question);

        // A requester has assistant.use but not admin.system.
        using (var forbidden = await SendAsync(app.Client, HttpMethod.Get, $"{AdminConversations}/{conversation}", token))
        {
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var forbiddenList = await SendAsync(app.Client, HttpMethod.Get, $"{AdminConversations}?userId=rita", token))
        {
            forbiddenList.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var admin = Admin(tenant);
        var page = await GetOkAsync(app.Client, $"{AdminConversations}?userId=rita&page=1", admin);
        page.GetProperty("total").GetInt32().ShouldBe(1);
        var item = page.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("id").GetGuid().ShouldBe(conversation);
        item.GetProperty("userId").GetString().ShouldBe("rita");
        (await GetOkAsync(app.Client, $"{AdminConversations}?userId=sam", admin)).GetProperty("total").GetInt32().ShouldBe(0);

        var record = await GetOkAsync(app.Client, $"{AdminConversations}/{conversation}", admin);
        record.GetProperty("userId").GetString().ShouldBe("rita");
        var answer = Messages(record)[1];
        answer.GetProperty("status").GetString().ShouldBe("Complete");
        answer.GetProperty("latencyMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        answer.GetProperty("timeToFirstTokenMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        answer.GetProperty("guardFlags").GetArrayLength().ShouldBe(0);
        answer.GetProperty("sources").GetArrayLength().ShouldBe(2);
    }

    /// <summary>Reading, asking in and deleting the conversation are all 404, the same as for an ID that never existed.</summary>
    private async Task ExpectHiddenAsync(string token, Guid conversation)
    {
        foreach (var id in new[] { conversation, Guid.NewGuid() })
        {
            using var get = await SendAsync(app.Client, HttpMethod.Get, $"{Conversations}/{id}", token);
            get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            var ask = await AskAsync(app.Client, token, id, "Show me this conversation.");
            ask.Status.ShouldBe(HttpStatusCode.NotFound);
            using var delete = await SendAsync(app.Client, HttpMethod.Delete, $"{Conversations}/{id}", token);
            delete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }
}
