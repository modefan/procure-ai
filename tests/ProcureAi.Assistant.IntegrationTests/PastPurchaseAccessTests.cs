using System.Text.Json;
using ProcureAi.Assistant.Application.Tools;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;
using static ProcureAi.Assistant.IntegrationTests.ChatApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// Past purchases are request data: an answer searches them, and the model is offered <c>find_similar_purchases</c>, only when the caller
/// has <c>requests.read</c> in the tenant's runtime permission matrix (Access). Forbidden stays forbidden when the matrix is edited.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class PastPurchaseAccessTests(AssistantApp app)
{
    private const string Number = "PR-0982";

    /// <summary>The question, unique per test (the recorded provider calls are shared by the collection).</summary>
    private static string Question(string tenant) => $"Were there similar purchases of laptops for the IT team {tenant}?";

    private async Task<(string Token, Guid Conversation)> SeededAsync(string tenant)
    {
        await SeedPolicyAsync(app, tenant, StreamingTests.ThresholdRule, StreamingTests.LaptopRule);
        await SearchSeed.AddPurchaseAsync(
            app, tenant, Number, "Laptops for the IT team", $"{Number} · Laptops for the IT team · IT-200 · laptops bought for the IT team");
        var token = User(tenant);
        return (token, await CreateAsync(app.Client, token));
    }

    private static List<JsonElement> AllSources(AskResult result) =>
        [.. result.Of("sources").SelectMany(e => e.Data.EnumerateArray())];

    private static List<string?> ToolCalls(AskResult result) =>
        [.. result.Of("tool_call").Select(e => e.Data.GetProperty("name").GetString())];

    [Fact]
    public async Task With_requests_read_past_purchases_are_sources_and_the_tool_is_offered()
    {
        var tenant = AssistantApp.NewTenant();
        var (token, conversation) = await SeededAsync(tenant);

        var result = await AskOkAsync(app.Client, token, conversation, Question(tenant));

        AllSources(result).ShouldContain(s => s.GetProperty("type").GetString() == "Purchase" && s.GetProperty("reference").GetString() == Number);
        ToolCalls(result).ShouldContain(AssistantTools.FindSimilarPurchases);
        var answer = app.Script.Calls.Last(c => c.Task == "answer" && c.LastUserText == Question(tenant));
        answer.Tools.ShouldContain(AssistantTools.FindSimilarPurchases);
    }

    [Fact]
    public async Task Without_requests_read_past_purchases_are_not_searched_and_the_tool_is_not_offered()
    {
        var tenant = AssistantApp.NewTenant();
        // The matrix was edited at runtime: requesters keep the assistant but lose requests.read.
        app.Access.SetMatrix(tenant, new Dictionary<string, string[]> { ["requester"] = [Permissions.AssistantUse, Permissions.BudgetsRead] });
        var (token, conversation) = await SeededAsync(tenant);

        var result = await AskOkAsync(app.Client, token, conversation, Question(tenant));

        AllSources(result).ShouldAllBe(s => s.GetProperty("type").GetString() == "Policy");
        ToolCalls(result).ShouldNotContain(AssistantTools.FindSimilarPurchases);
        result.StreamedText.ShouldNotContain(Number);
        var answers = app.Script.Calls.Where(c => c.Task == "answer" && c.LastUserText == Question(tenant)).ToList();
        answers.ShouldNotBeEmpty();
        answers.ShouldAllBe(c => !c.Tools.Contains(AssistantTools.FindSimilarPurchases));
        answers.ShouldAllBe(c => !c.AllText.Contains(Number));
    }
}
