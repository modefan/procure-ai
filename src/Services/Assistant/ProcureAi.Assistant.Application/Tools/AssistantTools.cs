using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Application.Telemetry;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Assistant.Application.Tools;

/// <summary>
/// The model's six read-only tools for one question (plan 05 Task 5b, ruling 3), as <see cref="AIFunction"/>s (<see cref="Functions"/>) over
/// the methods below; their <c>[Description]</c>s are what the model reads. Every lookup goes to another service <b>as the signed-in user</b>
/// (<c>AddProcureAiUserClient</c>: the user's token is relayed), so the callee's permissions and tenant filter decide what the model sees.
/// <para>
/// Every call: at most <c>Assistant:Tools:MaxCallsPerQuestion</c> per question (<see cref="ToolSession"/>; a call over the limit is refused
/// without a lookup); arguments validated before any lookup (cost center <c>^[A-Z]{2}-\d{3}$</c>, request number <c>^PR-\d{1,6}$</c>,
/// amount 0 &lt; x ≤ 100 000 000, text 1 to 200 characters); a timeout (<c>Assistant:Tools:Timeout</c>); the result is plain text cut to
/// <c>Assistant:Tools:MaxResultLength</c> and wrapped as <c>&lt;data kind="tool"&gt;</c> (<see cref="PromptBuilder.ToolData"/>). A failure
/// (401/403/404/5xx, a timeout, an open circuit, no credential) is an error result ("I couldn't check the budget: you don't have access
/// to IT-200."): a tool never throws into the model loop, except when the question itself was cancelled (the user left). Each call
/// counts <c>assistant.tool_calls</c> (tool, outcome) and logs one line without arguments; the span comes from the function invocation
/// stage of the pipeline.
/// </para>
/// </summary>
public sealed partial class AssistantTools
{
    public const string SearchPolicy = "search_policy";
    public const string ExplainApprovalChain = "explain_approval_chain";
    public const string GetRequestStatus = "get_request_status";
    public const string GetBudget = "get_budget";
    public const string FindSimilarPurchases = "find_similar_purchases";
    public const string GetSupplier = "get_supplier";

    /// <summary>How many policy sections <c>search_policy</c> returns.</summary>
    public const int PolicyHits = 4;

    /// <summary>How many past purchases <c>find_similar_purchases</c> returns.</summary>
    public const int PurchaseHits = 3;

    /// <summary>How many suppliers <c>get_supplier</c> returns.</summary>
    public const int SupplierHits = 3;

    public const decimal MaxAmount = 100_000_000m;
    public const int MaxTextLength = 200;

    private const int MaxSummaryLength = 120;

    private static readonly IReadOnlySet<SourceType> PolicyOnly = SearchRequest.PolicyOnly;
    private static readonly IReadOnlySet<SourceType> PurchasesOnly = SearchRequest.PurchasesOnly;

    private readonly ToolSession _session;
    private readonly IRequestsLookup _requests;
    private readonly IApprovalsLookup _approvals;
    private readonly IBudgetsLookup _budgets;
    private readonly ISuppliersLookup _suppliers;
    private readonly IHybridSearch _search;
    private readonly ToolOptions _options;
    private readonly TimeProvider _time;
    private readonly AssistantMetrics _metrics;
    private readonly ILogger _logger;
    private readonly bool _purchasesAllowed;

    /// <param name="purchasesAllowed">
    /// Whether the caller has <c>requests.read</c> (runtime matrix): past purchases are request data that this service reads from its own
    /// table, so no callee checks it. Without it <c>find_similar_purchases</c> is not offered, and a call to it is refused without a search.
    /// </param>
    public AssistantTools(
        ToolSession session, IRequestsLookup requests, IApprovalsLookup approvals, IBudgetsLookup budgets, ISuppliersLookup suppliers,
        IHybridSearch search, ToolOptions options, TimeProvider time, AssistantMetrics metrics, ILogger logger, bool purchasesAllowed)
    {
        (_session, _requests, _approvals, _budgets, _suppliers, _search) = (session, requests, approvals, budgets, suppliers, search);
        (_options, _time, _metrics, _logger, _purchasesAllowed) = (options, time, metrics, logger, purchasesAllowed);
        List<AIFunction> functions =
        [
            AIFunctionFactory.Create(SearchPolicyAsync, SearchPolicy),
            AIFunctionFactory.Create(ExplainApprovalChainAsync, ExplainApprovalChain),
            AIFunctionFactory.Create(GetRequestStatusAsync, GetRequestStatus),
            AIFunctionFactory.Create(GetBudgetAsync, GetBudget),
        ];
        if (purchasesAllowed)
        {
            functions.Add(AIFunctionFactory.Create(FindSimilarPurchasesAsync, FindSimilarPurchases));
        }

        functions.Add(AIFunctionFactory.Create(GetSupplierAsync, GetSupplier));
        Functions = functions;
    }

    /// <summary>The tools, for <see cref="ChatOptions.Tools"/>: six, or five without <c>requests.read</c>.</summary>
    public IReadOnlyList<AIFunction> Functions { get; }

    public ToolSession Session => _session;

    [Description(
        "Searches the organisation's procurement policy for more sections than were given. Returns the best sections, each with its source"
        + " marker such as [S7]; cite those markers like the other sources.")]
    public Task<string> SearchPolicyAsync(
        [Description("What to look for in the policy, in a few words.")] string query, CancellationToken cancellationToken) =>
        RunAsync(SearchPolicy, "search the policy", "the policy", async ct =>
        {
            var text = Text(query, "query");
            var hits = await _search.SearchAsync(new SearchRequest(text, PolicyOnly, PolicyHits), ct);
            if (hits.Count == 0)
            {
                return ToolResult.Success($"Nothing in the policy matches \"{text}\".", "No policy sections found");
            }

            // Every section gets its share of the result, so the last one is not cut off entirely.
            var share = Math.Max(80, (_options.MaxResultLength / hits.Count) - 60);
            var builder = new StringBuilder();
            var added = false;
            foreach (var hit in hits)
            {
                var (reference, isNew) = _session.AddSource(hit);
                added |= isNew;
                var section = hit.SectionPath.Count > 0 ? " › " + string.Join(Chunk.SectionSeparator, hit.SectionPath) : "";
                builder.Append('[').Append(reference).Append("] ").Append(hit.Title).Append(section).Append(": ")
                    .Append(Cut(Flatten(hit.ParentText), share)).Append('\n');
            }

            return new ToolResult(true, builder.ToString().TrimEnd(), $"{Count(hits.Count, "policy section")}") { AddsSources = added };
        }, cancellationToken);

    [Description(
        "Explains who would approve a purchase request of this amount on this cost center if the user raised it now: the approval steps"
        + " in order with the people who can approve each one, or why it cannot be approved.")]
    public Task<string> ExplainApprovalChainAsync(
        [Description("The request's total in rand including VAT, for example 150000.")] decimal amountInclVat,
        [Description("The cost center code, two letters, a dash and three digits, for example IT-200.")] string costCenter,
        CancellationToken cancellationToken) =>
        RunAsync(ExplainApprovalChain, "work out the approval chain", Normalised(costCenter), async ct =>
        {
            var code = CostCenter(costCenter);
            var amount = Amount(amountInclVat);
            var preview = await _approvals.PreviewAsync(code, amount, ct);
            var money = Money.Zar(amount).ToDisplayString();
            if (!preview.Possible)
            {
                return ToolResult.Success(
                    $"A request of {money} incl. VAT on {code} cannot be approved: {preview.Reason ?? "nobody may approve it."}", "Not possible");
            }

            var steps = preview.Steps.OrderBy(s => s.StepNo)
                .Select(s => $"{s.StepNo}. {Humanize(s.Level)}: {(s.ApproverNames.Count == 0 ? "nobody yet" : string.Join(" or ", s.ApproverNames))}");
            return ToolResult.Success(
                $"Approval chain for {money} incl. VAT on {code}: {string.Join("; ", steps)}.", Count(preview.Steps.Count, "approval step"));
        }, cancellationToken);

    [Description(
        "Looks up one purchase request by its number: its title, cost center, status, total and the approval step it is waiting for.")]
    public Task<string> GetRequestStatusAsync(
        [Description("The request number, for example PR-1043.")] string number, CancellationToken cancellationToken) =>
        RunAsync(GetRequestStatus, "check the request", Normalised(number), async ct =>
        {
            var requestNumber = RequestNumber(number);
            var request = await _requests.GetByNumberAsync(requestNumber, ct);
            var status = Humanize(request.Status);
            var text = $"{request.Number} · {request.Title} · {request.CostCenterCode} · {status} · "
                       + $"{Money.Zar(request.TotalInclVat).ToDisplayString()} incl. VAT. {await StepLineAsync(request.Id, ct)}";
            return ToolResult.Success(text, $"{request.Number}: {status}");
        }, cancellationToken);

    [Description(
        "Looks up a cost center's budget of the current fiscal year: the budget, what is spent, what is reserved by open requests and what"
        + " is still available.")]
    public Task<string> GetBudgetAsync(
        [Description("The cost center code, two letters, a dash and three digits, for example IT-200.")] string costCenter,
        CancellationToken cancellationToken) =>
        RunAsync(GetBudget, "check the budget", Normalised(costCenter), async ct =>
        {
            var code = CostCenter(costCenter);
            var year = FiscalYear.For(_time.GetUtcNow(), _options.FindTimeZone() ?? TimeZoneInfo.Utc).ToString();
            var budget = (await _budgets.ListAsync(year, ct))
                .FirstOrDefault(b => string.Equals(b.CostCenterCode, code, StringComparison.OrdinalIgnoreCase));
            if (budget is null)
            {
                return ToolResult.Success($"There is no budget for {code} in {year} that you can see.", "No budget found");
            }

            var available = Money.Zar(budget.Available).ToDisplayString();
            return ToolResult.Success(
                $"Budget of {code} for {year}: {Money.Zar(budget.Amount).ToDisplayString()}; spent {Money.Zar(budget.Spent).ToDisplayString()};"
                + $" reserved {Money.Zar(budget.Reserved).ToDisplayString()}; available {available}.",
                $"{available} available");
        }, cancellationToken);

    [Description("Finds past completed purchases similar to a description: their number, title, cost center, total and approvers.")]
    public Task<string> FindSimilarPurchasesAsync(
        [Description("What was bought, in a few words, for example \"laptops for the IT team\".")] string description,
        CancellationToken cancellationToken) =>
        RunAsync(FindSimilarPurchases, "search past purchases", "past purchases", async ct =>
        {
            if (!_purchasesAllowed)
            {
                throw new ToolLookupException(ToolFailure.Forbidden, "The caller has no requests.read.");
            }

            var text = Text(description, "description");
            var hits = await _search.SearchAsync(new SearchRequest(text, PurchasesOnly, PurchaseHits), ct);
            if (hits.Count == 0)
            {
                return ToolResult.Success("No similar past purchases found.", "No past purchases found");
            }

            return ToolResult.Success(
                string.Join('\n', hits.Select(h => "- " + Flatten(h.ParentText))), Count(hits.Count, "past purchase"));
        }, cancellationToken);

    [Description("Looks up suppliers in the catalog by name or code: the first three matches with how many items each offers.")]
    public Task<string> GetSupplierAsync(
        [Description("The supplier's name or code, or part of it.")] string name, CancellationToken cancellationToken) =>
        RunAsync(GetSupplier, "look up the supplier", "the supplier catalog", async ct =>
        {
            var text = Text(name, "name");
            var found = await _suppliers.SearchAsync(text, SupplierHits, ct);
            if (found.Count == 0)
            {
                return ToolResult.Success($"No supplier matches \"{text}\".", "No suppliers found");
            }

            var shown = found.Take(SupplierHits).ToList();
            return ToolResult.Success(
                string.Join('\n', shown.Select(s => $"- {s.Name} ({s.Code}): {Count(s.ItemCount, "item")}, {(s.Active ? "active" : "inactive")}")),
                $"{Count(shown.Count, "supplier")}: {string.Join(", ", shown.Select(s => s.Name))}");
        }, cancellationToken);

    /// <summary>The approval step the request waits for, or what its chain says; a failed chain lookup does not fail the request lookup.</summary>
    private async Task<string> StepLineAsync(Guid requestId, CancellationToken cancellationToken)
    {
        try
        {
            var chain = await _approvals.ChainAsync(requestId, cancellationToken);
            return chain.Steps.OrderBy(s => s.StepNo).FirstOrDefault(s => s.Status == "Pending") is { } open
                ? $"Waiting for step {open.StepNo}: {Humanize(open.Level)}."
                : $"Approval: {Humanize(chain.State)}.";
        }
        catch (ToolLookupException failure) when (failure.Failure == ToolFailure.NotFound)
        {
            return "It has no approval chain yet.";
        }
        catch (ToolLookupException)
        {
            return "Its approval step could not be checked.";
        }
    }

    /// <summary>
    /// One call: the limit, the timeout, the failures as error results, the cut and the wrapper, the outcome for the answer flow (by the
    /// model's call ID), the metric and the log line.
    /// </summary>
    private async Task<string> RunAsync(
        string tool, string action, string subject, Func<CancellationToken, Task<ToolResult>> body, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        string outcome;
        ToolResult result;
        if (!_session.TryStartCall())
        {
            (outcome, result) = ("limit", ToolResult.Error(
                $"I couldn't {action}: the limit of {_session.MaxCalls} lookups for one question is reached. Answer with what you have."));
        }
        else
        {
            using var timeout = new CancellationTokenSource(_options.Timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                result = await body(linked.Token);
                outcome = "ok";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The question was stopped (the user left): the answer flow ends, nothing to report.
                throw;
            }
            catch (Exception exception)
            {
                (outcome, result) = Failed(exception, timeout.IsCancellationRequested, action, subject);
                if (outcome == "failed")
                {
                    _logger.LogError(exception, "Tool {Tool} failed unexpectedly", tool);
                }
            }
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? Guid.NewGuid().ToString("N");
        _session.Record(callId, new ToolOutcome(tool, result.Ok, Cut(result.Summary, MaxSummaryLength), durationMs, outcome, result.AddsSources));
        _metrics.ToolCall(tool, outcome);
        _logger.LogInformation("Tool {Tool} {Outcome} in {DurationMs} ms", tool, outcome, durationMs);
        // Escaped before the cut, so the escapes count towards the length (ToolData's own escape then finds nothing left to escape).
        return PromptBuilder.ToolData(tool, Cut(PromptBuilder.Escape(result.Text), _options.MaxResultLength));
    }

    private static (string Outcome, ToolResult Result) Failed(Exception exception, bool timedOut, string action, string subject)
    {
        var (outcome, reason) = exception switch
        {
            InvalidToolArgumentException invalid => ("invalid", invalid.Message),
            OperationCanceledException or TimeoutException when timedOut => ("timeout", "it took too long. Try again later."),
            UserCredentialMissingException => ("unauthorized", "there is no signed-in user to check it for."),
            ToolLookupException { Failure: ToolFailure.Forbidden } => ("forbidden", $"you don't have access to {subject}."),
            ToolLookupException { Failure: ToolFailure.NotFound } => ("not_found", $"{subject} was not found."),
            ToolLookupException { Failure: ToolFailure.Unauthorized } => ("unauthorized", "your sign-in was not accepted. Sign in again."),
            ToolLookupException => ("unavailable", "the service is not available right now. Try again later."),
            _ => ("failed", "something went wrong. Try again later."),
        };
        return (outcome, ToolResult.Error($"I couldn't {action}: {reason}"));
    }

    private static string Normalised(string? value) => (value ?? "").Trim().ToUpperInvariant() is { Length: > 0 and <= 20 } text ? text : "it";

    private static string CostCenter(string? value)
    {
        var code = (value ?? "").Trim().ToUpperInvariant();
        return CostCenterPattern().IsMatch(code)
            ? code
            : throw new InvalidToolArgumentException($"\"{Cut(value ?? "", 20)}\" is not a cost center code like IT-200.");
    }

    private static string RequestNumber(string? value)
    {
        var number = (value ?? "").Trim().ToUpperInvariant();
        return RequestNumberPattern().IsMatch(number)
            ? number
            : throw new InvalidToolArgumentException($"\"{Cut(value ?? "", 20)}\" is not a request number like PR-1043.");
    }

    private static decimal Amount(decimal amount) =>
        amount is > 0 and <= MaxAmount
            ? amount
            : throw new InvalidToolArgumentException(
                $"the amount must be more than {Money.Zero.ToDisplayString()} and at most {Money.Zar(MaxAmount).ToDisplayString()}.");

    private static string Text(string? value, string name)
    {
        var text = Flatten(value ?? "");
        return text.Length is > 0 and <= MaxTextLength
            ? text
            : throw new InvalidToolArgumentException($"the {name} must be 1 to {MaxTextLength} characters.");
    }

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    /// <summary><c>DepartmentHead</c> → "Department head", <c>AwaitingApproval</c> → "Awaiting approval".</summary>
    private static string Humanize(string value)
    {
        var words = PascalWord().Matches(value ?? "").Select(m => m.Value).ToList();
        if (words.Count == 0)
        {
            return value ?? "";
        }

        return string.Join(' ', words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));
    }

    private static string Flatten(string text) => Whitespace().Replace(text, " ").Trim();

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"^[A-Z]{2}-\d{3}$")]
    private static partial Regex CostCenterPattern();

    [GeneratedRegex(@"^PR-\d{1,6}$")]
    private static partial Regex RequestNumberPattern();

    [GeneratedRegex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+")]
    private static partial Regex PascalWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The model's argument is not acceptable; nothing was looked up.</summary>
    private sealed class InvalidToolArgumentException(string message) : Exception(message);
}

/// <summary>Builds the <see cref="AssistantTools"/> of one question (the answer flow, <see cref="AnswerStreamer"/>).</summary>
public sealed class AssistantToolsFactory(
    IRequestsLookup requests, IApprovalsLookup approvals, IBudgetsLookup budgets, ISuppliersLookup suppliers, IHybridSearch search,
    IOptions<AssistantOptions> options, TimeProvider time, AssistantMetrics metrics, ILogger<AssistantTools> logger)
{
    /// <summary>
    /// The tools of a question whose answer already has <paramref name="sources"/> (S1…), with a fresh call budget; past purchases only when
    /// <paramref name="purchasesAllowed"/> (the caller has <c>requests.read</c>).
    /// </summary>
    public AssistantTools Create(IReadOnlyList<SourceRef> sources, bool purchasesAllowed) =>
        new(new ToolSession(sources, options.Value.Tools.MaxCallsPerQuestion), requests, approvals, budgets, suppliers, search, options.Value.Tools,
            time, metrics, logger, purchasesAllowed);
}
