using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace ProcureAi.SystemTests;

/// <summary>
/// The Assistant in the whole system (plan 05 Task 10), on the Fake provider the AppHost runs by default: the seeded procurement policy is
/// ingested (Ready), a question asked through the gateway streams an answer with policy sources and a tool step (the tool calls Approvals as
/// the asking user), and a request that completes becomes a past purchase the assistant cites.
/// </summary>
[Collection(SystemCollection.Name)]
public sealed class AssistantFlowTests(SystemFixture system)
{
    /// <summary>The acme copy of the seeded policy (DemoSeeder of the Assistant).</summary>
    private static readonly Guid AcmePolicy = Guid.Parse("5eed0000-0000-4000-8000-00000000a001");

    // 1 × MON-27 at R 4 200,00 = R 4 830,00 incl. VAT: Maria alone approves.
    private const decimal MonitorInclVat = 4_830.00m;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SystemFlow _flow = new(system);

    [Fact]
    public async Task Seeded_policy_is_ready()
    {
        system.RequireReady();
        using var gateway = system.Gateway();

        var document = await SystemFlow.EventuallyAsync(
            () => GetJsonAsync(gateway, $"/api/v1/assistant/policy-documents/{AcmePolicy}", onBehalfOf: null),
            d => d.GetProperty("status").GetString() is "Ready" or "Failed" or "Flagged",
            "the seeded policy ingested", TimeSpan.FromMinutes(3), retryNotFound: true);

        document.GetProperty("status").GetString().ShouldBe("Ready");
        document.GetProperty("title").GetString().ShouldBe("Procurement policy");
        document.GetProperty("chunkCount").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Streamed_answer_through_the_gateway_has_sources_and_a_tool_step()
    {
        system.RequireReady();
        using var gateway = system.Gateway();
        await SystemFlow.EventuallyAsync(
            () => GetJsonAsync(gateway, $"/api/v1/assistant/policy-documents/{AcmePolicy}", onBehalfOf: null),
            d => d.GetProperty("status").GetString() == "Ready", "the seeded policy ready", TimeSpan.FromMinutes(3), retryNotFound: true);
        var conversation = await CreateConversationAsync(gateway, SystemFixture.Tom);

        // An amount and a cost center: the Fake model calls explain_approval_chain (as Tom, through Approvals), then answers from the policy.
        var events = await AskAsync(gateway, SystemFixture.Tom, conversation, "Who approves R 250 000 for IT-200?");

        var names = events.Select(e => e.Name).ToList();
        names.ShouldContain("tool_call");
        names.ShouldContain("tool_result");
        names.ShouldContain("delta");
        names[^1].ShouldBe("done", string.Join(", ", names));
        names.IndexOf("sources").ShouldBeLessThan(names.IndexOf("delta"), "the sources come before any text");

        var call = events.First(e => e.Name == "tool_call").Data;
        call.GetProperty("name").GetString().ShouldBe("explain_approval_chain");
        var result = events.First(e => e.Name == "tool_result").Data;
        result.GetProperty("ok").GetBoolean().ShouldBeTrue(result.GetRawText());

        var sources = events.Last(e => e.Name == "sources").Data.EnumerateArray().ToList();
        sources.ShouldNotBeEmpty();
        sources.ShouldContain(s => s.GetProperty("type").GetString() == "Policy"
                                   && s.GetProperty("sectionPath").EnumerateArray().Any(p => p.GetString() == "4.2 Approval thresholds"));

        var text = string.Concat(events.Where(e => e.Name == "delta").Select(e => e.Data.GetProperty("text").GetString()));
        text.ShouldContain("[S");
        text.ShouldContain("| --- |", customMessage: "the thresholds table is quoted as a Markdown table");
        text.ShouldContain("From explain_approval_chain:");
        events[^1].Data.GetProperty("model").GetString().ShouldBe("fake-chat");

        // The answer is saved with the conversation.
        var saved = await GetJsonAsync(gateway, $"/api/v1/assistant/conversations/{conversation}", SystemFixture.Tom);
        var answer = saved.GetProperty("messages").EnumerateArray().Last();
        answer.GetProperty("status").GetString().ShouldBe("Complete");
        answer.GetProperty("toolSteps").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Completed_request_becomes_a_purchase_source()
    {
        system.RequireReady();
        var marker = $"Assistant monitor {Guid.NewGuid():N}"[..30];

        // Tom's monitor goes all the way: approved, ordered, invoiced by the supplier, received, paid, completed.
        var request = await _flow.SubmitAsync(marker, "MON-27", 1, MonitorInclVat);
        await _flow.ApproveAsync(SystemFixture.Maria, request.Id, 1, "Manager", "R 4 830,00");
        var (order, _) = await _flow.OrderWhenAsync(request.Id, o => o.Status == "Confirmed", "order confirmed");
        using var thandi = system.Service("invoicing", SystemFixture.Thandi);
        await SystemFlow.InvoiceWhenAsync(thandi, order.Id, _ => true, "the supplier's invoice");
        using var tomPurchasing = system.Service("purchasing", SystemFixture.Tom);
        var (_, orderTag) = await tomPurchasing.GetAsync<OrderView>($"/api/v1/purchasing/orders/{order.Id}");
        await tomPurchasing.SendAsync<OrderView>(
            HttpMethod.Post, $"/api/v1/purchasing/orders/{order.Id}/receipts", HttpStatusCode.OK,
            new { lines = new[] { new { lineNo = 1, quantity = 1 } }, deliveryNote = "DN-A1" }, orderTag);
        var invoice = await SystemFlow.InvoiceWhenAsync(thandi, order.Id, i => i.Status == "Matched", "invoice matched after the receipt");
        var (current, _) = await thandi.GetAsync<InvoiceView>($"/api/v1/invoicing/invoices/{invoice.Id}");
        await thandi.SendAsync<InvoiceView>(
            HttpMethod.Post, $"/api/v1/invoicing/invoices/{invoice.Id}/payments", HttpStatusCode.OK, ifMatch: $"\"{current.Version}\"");
        await _flow.RequestWhenAsync(request.Id, r => r.Status == "Completed", "request completed");

        // The Assistant indexes it from RequestCompleted; asked about it, the answer cites it as a past purchase.
        using var gateway = system.Gateway();
        var conversation = await CreateConversationAsync(gateway, SystemFixture.Thandi);
        // Each try is a question against Thandi's daily quota, so it polls slowly.
        JsonElement? purchase = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (purchase is null && DateTime.UtcNow < deadline)
        {
            var sources = (await AskAsync(gateway, SystemFixture.Thandi, conversation, marker))
                .Where(e => e.Name == "sources").SelectMany(e => e.Data.EnumerateArray()).ToList();
            purchase = sources.Where(s => s.GetProperty("type").GetString() == "Purchase" && s.GetProperty("reference").GetString() == request.Number)
                .Select(s => (JsonElement?)s).FirstOrDefault();
            if (purchase is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(6), Ct);
            }
        }

        purchase.ShouldNotBeNull($"{request.Number} cited as a past purchase within 2 minutes");
        purchase.Value.GetProperty("title").GetString().ShouldBe(marker);
    }

    private sealed record Event(string Name, JsonElement Data);

    private HttpRequestMessage Message(HttpMethod method, string path, string? onBehalfOf, object? body = null)
    {
        var message = new HttpRequestMessage(method, path);
        message.Headers.Add("X-Api-Key", system.AdminApiKey);
        message.Headers.Add("X-Tenant-Id", SystemFixture.Tenant);
        if (onBehalfOf is not null)
        {
            message.Headers.Add("X-On-Behalf-Of-User", onBehalfOf);
        }

        if (method == HttpMethod.Post)
        {
            message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            message.Content = JsonContent.Create(body, options: ServiceClient.Json);
        }

        return message;
    }

    private async Task<JsonElement> GetJsonAsync(HttpClient gateway, string path, string? onBehalfOf)
    {
        using var message = Message(HttpMethod.Get, path, onBehalfOf);
        using var response = await gateway.SendAsync(message, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpStatusException(HttpMethod.Get, path, response.StatusCode, $"expected 200, got {(int)response.StatusCode}: {text}");
        }

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<Guid> CreateConversationAsync(HttpClient gateway, string user)
    {
        using var message = Message(HttpMethod.Post, "/api/v1/assistant/conversations", user);
        using var response = await gateway.SendAsync(message, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Asks a question and reads the whole server-sent event stream (it ends after <c>done</c> or <c>error</c>).</summary>
    private async Task<IReadOnlyList<Event>> AskAsync(HttpClient gateway, string user, Guid conversation, string question)
    {
        var path = $"/api/v1/assistant/conversations/{conversation}/messages";
        using var message = Message(HttpMethod.Post, path, user, new { text = question, mode = "New" });
        using var response = await gateway.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpStatusException(HttpMethod.Post, path, response.StatusCode, $"expected 200, got {(int)response.StatusCode}: {body}");
        }

        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");
        var events = new List<Event>();
        foreach (var block in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            var data = new StringBuilder();
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    name = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line["data:".Length..].TrimStart());
                }
            }

            if (name is not null)
            {
                events.Add(new Event(name, JsonDocument.Parse(data.ToString()).RootElement.Clone()));
            }
        }

        events.ShouldNotContain(e => e.Name == "error", string.Join(" ", events.Where(e => e.Name == "error").Select(e => e.Data.GetRawText())));
        return events;
    }
}
