using System.Diagnostics;
using System.Text.Json;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;
using static ProcureAi.Assistant.IntegrationTests.ChatApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// The model's read-only tools in the answer stream (plan 05 Task 5b, ruling 3): they call the stub services as the signed-in user (the
/// relayed token is asserted at the stubs), their calls and results are events and saved steps, failures are results, and the limits hold.
/// The Fake model picks a tool by its intent rules; <see cref="ChatScript"/> words make it loop or burst.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class ToolTests(AssistantApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CurrentYear => FiscalYear.For(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg")).ToString();

    private static string R(decimal amount) => Money.Zar(amount).ToDisplayString();

    private async Task<(string Tenant, string Token, Guid Conversation)> NewConversationAsync(string user = "rita")
    {
        var tenant = AssistantApp.NewTenant();
        await SeedPolicyAsync(app, tenant, StreamingTests.ThresholdRule, StreamingTests.LaptopRule);
        var token = User(tenant, user);
        return (tenant, token, await CreateAsync(app.Client, token));
    }

    private static JsonElement Single(AskResult result, string name) => result.Of(name).ShouldHaveSingleItem().Data;

    [Fact]
    public async Task Tools_relay_the_users_bearer_token()
    {
        var (tenant, token, conversation) = await NewConversationAsync();
        app.Budgets.SetBudgets(tenant, new StubBudget("IT-200", CurrentYear, 500_000m, 100_000m, 50_000m));
        var requestId = Guid.NewGuid();
        app.Requests.SetRequest(tenant, new StubRequest(requestId, "PR-1043", "Laptops for IT", "Submitted", "IT-200", 86_940m));
        app.Approvals.SetChain(tenant, requestId, "AwaitingApproval", new StubChainStep(1, "Manager", "Pending"));

        var budget = await AskOkAsync(app.Client, token, conversation, "What is left in the budget of IT-200?");
        var status = await AskOkAsync(app.Client, token, conversation, "Where is PR-1043?");

        // Every call reached its service with the user's own token, as the Assistant (never a service token or the admin key).
        var calls = app.Budgets.CallsOf(tenant).Concat(app.Requests.CallsOf(tenant)).Concat(app.Approvals.UserCallsOf(tenant)).ToList();
        calls.Count.ShouldBe(3);
        calls.ShouldAllBe(c => c.Authorization == "Bearer " + token && c.ApiKey == null && c.CallingService == "assistant-api" && c.User == "rita");
        app.Budgets.CallsOf(tenant).Single().Path.ShouldBe($"/api/v1/budgets?fiscalYear={CurrentYear}");
        Single(budget, "tool_result").GetProperty("summary").GetString().ShouldBe($"{R(350_000m)} available");
        // The Fake flattens whitespace (the no-break spaces of the amounts too).
        budget.StreamedText.ShouldContain($"available {R(350_000m)}".Replace('\u00A0', ' '));
        Single(status, "tool_result").GetProperty("summary").GetString().ShouldBe("PR-1043: Submitted");
        status.StreamedText.ShouldContain("Waiting for step 1: Manager.");
    }

    [Fact]
    public async Task Requester_cannot_see_other_cost_center_budget_through_tool()
    {
        var (tenant, token, conversation) = await NewConversationAsync("rita");
        app.Budgets.SetBudgets(tenant, new StubBudget("FI-300", CurrentYear, 900_000m, 0m, 0m));
        app.Budgets.SetForbidden(tenant, "rita");

        var result = await AskOkAsync(app.Client, token, conversation, "What is left in the budget of FI-300?");

        var toolResult = Single(result, "tool_result");
        toolResult.GetProperty("ok").GetBoolean().ShouldBeFalse();
        toolResult.GetProperty("summary").GetString().ShouldBe("I couldn't check the budget: you don't have access to FI-300.");
        result.StreamedText.ShouldNotContain(R(900_000m));
        app.Budgets.CallsOf(tenant).Single().Authorization.ShouldBe("Bearer " + token);

        var saved = Messages(await ConversationAsync(app.Client, token, conversation))[^1];
        var step = saved.GetProperty("toolSteps").EnumerateArray().ShouldHaveSingleItem();
        step.GetProperty("name").GetString().ShouldBe("get_budget");
        step.GetProperty("ok").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Stream_shows_tool_call_and_result_events_with_duration()
    {
        var (tenant, token, conversation) = await NewConversationAsync();
        app.Budgets.SetBudgets(tenant, new StubBudget("IT-200", CurrentYear, 500_000m, 0m, 0m));

        var result = await AskOkAsync(app.Client, token, conversation, "What is left in the budget of IT-200?");

        var names = result.Frames.Select(f => f.Event).ToList();
        names[..3].ShouldBe(["sources", "tool_call", "tool_result"]);
        names[^1].ShouldBe("done");
        names.Skip(3).SkipLast(1).ShouldAllBe(n => n == "delta");
        var call = Single(result, "tool_call");
        var toolResult = Single(result, "tool_result");
        call.GetProperty("name").GetString().ShouldBe("get_budget");
        call.GetProperty("arguments").GetProperty("costCenter").GetString().ShouldBe("IT-200");
        toolResult.GetProperty("id").GetString().ShouldBe(call.GetProperty("id").GetString());
        toolResult.GetProperty("ok").GetBoolean().ShouldBeTrue();
        toolResult.GetProperty("durationMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        toolResult.GetProperty("durationMs").GetInt64().ShouldBeLessThan(5_000);

        // Saved with the answer: what was asked and how it went (no result data).
        var saved = Messages(await ConversationAsync(app.Client, token, conversation))[^1];
        var step = saved.GetProperty("toolSteps").EnumerateArray().ShouldHaveSingleItem();
        step.GetProperty("id").GetString().ShouldBe(call.GetProperty("id").GetString());
        step.GetProperty("summary").GetString().ShouldBe($"{R(500_000m)} available");
        JsonDocument.Parse(step.GetProperty("arguments").GetString()!).RootElement.GetProperty("costCenter").GetString().ShouldBe("IT-200");

        // The model got the result as a function result wrapped as data, and the function invocation was traced.
        var final = app.Script.Calls.Last(c => c.Task == AiTasks.Answer && c.Messages.Any(m => m.Text.Contains("budget of IT-200", StringComparison.Ordinal)));
        var functionResult = final.Messages.SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionResultContent>().Single();
        functionResult.Result!.ToString()!.ShouldStartWith("<data kind=\"tool\" name=\"get_budget\">");
        List<Activity> spans;
        lock (app.ExportedActivities)
        {
            spans = [.. app.ExportedActivities];
        }

        spans.ShouldContain(a => a.DisplayName.Contains("execute_tool", StringComparison.Ordinal) && a.DisplayName.Contains("get_budget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sixth_tool_call_is_not_made()
    {
        var (tenant, token, conversation) = await NewConversationAsync();
        app.Budgets.SetBudgets(tenant, new StubBudget("IT-200", CurrentYear, 500_000m, 0m, 0m));

        // One call per turn, for ever: five calls are made, then the model answers without tools.
        var loop = await AskOkAsync(app.Client, token, conversation, $"{ChatScript.Loop} keep checking");

        app.Budgets.CallsOf(tenant).Count.ShouldBe(5);
        loop.Of("tool_result").Count().ShouldBe(5);
        loop.Of("tool_result").ShouldAllBe(f => f.Data.GetProperty("ok").GetBoolean());
        var answerCalls = app.Script.Calls.Where(c => c.Task == AiTasks.Answer && c.LastUserText.Contains(ChatScript.Loop, StringComparison.Ordinal)).ToList();
        answerCalls.Count.ShouldBe(6);
        answerCalls[^1].Tools.ShouldBeEmpty("the last model call is made without tools");

        // Six calls in one turn: five are made, the sixth is refused without a lookup.
        var (burstTenant, burstToken, burstConversation) = await NewConversationAsync();
        app.Budgets.SetBudgets(burstTenant, new StubBudget("IT-200", CurrentYear, 500_000m, 0m, 0m));
        var burst = await AskOkAsync(app.Client, burstToken, burstConversation, $"{ChatScript.Burst} all at once");

        app.Budgets.CallsOf(burstTenant).Count.ShouldBe(5);
        var results = burst.Of("tool_result").Select(f => f.Data).ToList();
        results.Count.ShouldBe(6);
        results.Count(r => r.GetProperty("ok").GetBoolean()).ShouldBe(5);
        results.Single(r => !r.GetProperty("ok").GetBoolean()).GetProperty("summary").GetString()
            .ShouldBe("I couldn't check the budget: the limit of 5 lookups for one question is reached. Answer with what you have.");
    }

    [Fact]
    public async Task Tool_timeout_returns_error_and_answer_continues()
    {
        var (tenant, token, conversation) = await NewConversationAsync();
        app.Budgets.SetHanging(tenant);

        var result = await AskOkAsync(app.Client, token, conversation, "What is left in the budget of IT-200?");

        var toolResult = Single(result, "tool_result");
        toolResult.GetProperty("ok").GetBoolean().ShouldBeFalse();
        toolResult.GetProperty("summary").GetString().ShouldBe("I couldn't check the budget: it took too long. Try again later.");
        toolResult.GetProperty("durationMs").GetInt64().ShouldBeInRange(1_900, 6_000);
        // The answer went on: the model got the error as the tool's result and answered with it.
        result.StreamedText.ShouldContain("it took too long");
        result.Done.Data.GetProperty("messageId").GetGuid().ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Search_policy_adds_sources_numbered_on_and_a_further_sources_event()
    {
        var tenant = AssistantApp.NewTenant();
        // Seven travel rules fill the answer's own sources; the one about submarines only the tool's search finds.
        string[] travel = [.. Enumerable.Range(1, 7).Select(i => $"Travel rule {i}: flights and hotels for trip type {i} need a manager's approval.")];
        await SeedPolicyAsync(app, tenant, [.. travel, "Submarines and other vessels are bought only by the board."]);
        var token = User(tenant);
        var conversation = await CreateAsync(app.Client, token);

        var result = await AskOkAsync(app.Client, token, conversation, $"{ChatScript.PolicyLookup} which travel needs a manager's approval?");

        var events = result.Of("sources").ToList();
        events.Count.ShouldBe(2);
        var first = events[0].Data.EnumerateArray().Select(s => s.GetProperty("ref").GetString()).ToList();
        var all = events[1].Data.EnumerateArray().ToList();
        first.Count.ShouldBe(6);
        all.Select(s => s.GetProperty("ref").GetString()).Take(6).ShouldBe(first);
        all.Count.ShouldBeGreaterThan(6);
        all[6].GetProperty("ref").GetString().ShouldBe("S7");
        result.Frames.Select(f => f.Event).ToList().IndexOf("tool_result")
            .ShouldBeLessThan(result.Frames.Select(f => f.Event).ToList().LastIndexOf("sources"));
        result.StreamedText.ShouldContain("[S7]");
        result.StreamedText.ShouldContain("Submarines");

        var saved = Messages(await ConversationAsync(app.Client, token, conversation))[^1];
        saved.GetProperty("sources").GetArrayLength().ShouldBe(all.Count);
    }

    [Fact]
    public async Task Tool_result_summary_with_a_secret_is_hidden()
    {
        var (tenant, token, conversation) = await NewConversationAsync();
        app.Suppliers.SetSupplier(tenant, "acme", "Acme sales.manager@acme-supplies.co.za", true, new StubCatalogItem("A", "a", 10m));

        var result = await AskAsync(app.Client, token, conversation, "Tell me about the supplier acme");

        var toolResult = Single(result, "tool_result");
        toolResult.GetProperty("ok").GetBoolean().ShouldBeTrue();
        toolResult.GetProperty("summary").GetString().ShouldBe(AnswerStreamer.HiddenSummary);
        result.Frames.ShouldAllBe(f => !f.Data.GetRawText().Contains("acme-supplies.co.za", StringComparison.Ordinal));
        app.Suppliers.UserCallsOf(tenant).Single().Authorization.ShouldBe("Bearer " + token);

        var saved = await WaitForLastMessageAsync(app.Client, token, conversation, m => m.GetProperty("role").GetString() == "Assistant");
        saved.GetProperty("toolSteps").EnumerateArray().Single().GetProperty("summary").GetString().ShouldBe(AnswerStreamer.HiddenSummary);
    }
}
