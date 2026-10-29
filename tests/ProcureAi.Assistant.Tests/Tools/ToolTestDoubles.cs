using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Application.Telemetry;
using ProcureAi.Assistant.Application.Tools;

namespace ProcureAi.Assistant.Tests.Tools;

/// <summary>The four lookups in one, answering from delegates a test sets and recording every call ("budgets FY2027").</summary>
internal sealed class FakeLookups : IRequestsLookup, IApprovalsLookup, IBudgetsLookup, ISuppliersLookup
{
    private readonly ConcurrentQueue<string> _calls = new();

    public List<string> Calls => [.. _calls];

    public Func<string, RequestStatusInfo> Request { get; set; } = _ => throw new ToolLookupException(ToolFailure.NotFound, "none");

    public Func<string, decimal, ApprovalPreviewInfo> Preview { get; set; } = (_, _) => new ApprovalPreviewInfo(true, null, []);

    public Func<Guid, ApprovalChainInfo> Chain { get; set; } = _ => throw new ToolLookupException(ToolFailure.NotFound, "none");

    public Func<string, IReadOnlyList<BudgetInfo>> Budgets { get; set; } = _ => [];

    /// <summary>When set, answers the budgets instead of <see cref="Budgets"/> (with the call's cancellation token).</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<BudgetInfo>>>? BudgetsAsync { get; set; }

    public Func<string, int, IReadOnlyList<SupplierInfo>> Suppliers { get; set; } = (_, _) => [];

    public Task<RequestStatusInfo> GetByNumberAsync(string number, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"request {number}");
        return Task.FromResult(Request(number));
    }

    public Task<ApprovalPreviewInfo> PreviewAsync(string costCenter, decimal amountInclVat, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"preview {costCenter} {amountInclVat.ToString(CultureInfo.InvariantCulture)}");
        return Task.FromResult(Preview(costCenter, amountInclVat));
    }

    public Task<ApprovalChainInfo> ChainAsync(Guid requestId, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"chain {requestId}");
        return Task.FromResult(Chain(requestId));
    }

    public Task<IReadOnlyList<BudgetInfo>> ListAsync(string fiscalYear, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"budgets {fiscalYear}");
        return BudgetsAsync is { } slow ? slow(fiscalYear, cancellationToken) : Task.FromResult(Budgets(fiscalYear));
    }

    public Task<IReadOnlyList<SupplierInfo>> SearchAsync(string search, int pageSize, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"suppliers {search} {pageSize}");
        return Task.FromResult(Suppliers(search, pageSize));
    }
}

/// <summary>A hybrid search that returns <see cref="Hits"/> (cut to the request's top K) and records the requests.</summary>
internal sealed class FakeSearch : IHybridSearch
{
    private readonly ConcurrentQueue<SearchRequest> _requests = new();

    public IReadOnlyList<SearchHit> Hits { get; set; } = [];

    public List<SearchRequest> Requests => [.. _requests];

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request);
        return Task.FromResult<IReadOnlyList<SearchHit>>([.. Hits.Take(request.TopK)]);
    }
}

/// <summary>Counts the <c>assistant.tool_calls</c> measurements by tool and outcome.</summary>
internal sealed class ToolCallCounter : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Tool, string Outcome)> _seen = new();

    public ToolCallCounter(IMeterFactory meters)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == AssistantMetrics.ToolCalls && ReferenceEquals(instrument.Meter.Scope, meters))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString() ?? "");
            for (var i = 0; i < value; i++)
            {
                _seen.Enqueue((map["tool"], map["outcome"]));
            }
        });
        _listener.Start();
    }

    public int Count(string tool, string outcome) => _seen.Count(s => s.Tool == tool && s.Outcome == outcome);

    public void Dispose() => _listener.Dispose();
}
