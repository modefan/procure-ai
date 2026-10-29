using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Tools;

/// <summary>How one tool call went, for the <c>tool_result</c> event and the saved <c>ToolStep</c>.</summary>
/// <param name="Outcome">The metric's outcome tag: <c>ok</c>, <c>invalid</c>, <c>forbidden</c>, <c>unauthorized</c>, <c>not_found</c>,
/// <c>unavailable</c>, <c>timeout</c>, <c>limit</c> or <c>failed</c>.</param>
/// <param name="AddedSources">Whether the call added sources (<c>search_policy</c>), so a further <c>sources</c> event is due.</param>
public sealed record ToolOutcome(string Tool, bool Ok, string Summary, long DurationMs, string Outcome, bool AddedSources);

/// <summary>
/// The tool state of one question (<see cref="AssistantTools"/>): the sources so far (the retrieved ones, then what <c>search_policy</c>
/// adds, numbered on: S7, S8…), the calls made against <c>Assistant:Tools:MaxCallsPerQuestion</c>, and each call's outcome by the model's
/// call ID until the answer flow takes it. Thread-safe (the model may ask for several calls in one turn).
/// </summary>
public sealed class ToolSession(IReadOnlyList<SourceRef> sources, int maxCalls)
{
    private readonly Lock _gate = new();
    private readonly List<SourceRef> _sources = [.. sources];
    private readonly Dictionary<string, ToolOutcome> _outcomes = new(StringComparer.Ordinal);
    private int _calls;
    private string? _lastCallId;

    public int MaxCalls { get; } = maxCalls;

    /// <summary>The calls that were let through (a call refused for the limit is not counted).</summary>
    public int CallsMade
    {
        get
        {
            lock (_gate)
            {
                return _calls;
            }
        }
    }

    /// <summary>Every source of the answer so far, in marker order.</summary>
    public IReadOnlyList<SourceRef> Sources
    {
        get
        {
            lock (_gate)
            {
                return [.. _sources];
            }
        }
    }

    /// <summary>Counts a call; false (and not counted) once <see cref="MaxCalls"/> calls were made.</summary>
    public bool TryStartCall()
    {
        lock (_gate)
        {
            if (_calls >= MaxCalls)
            {
                return false;
            }

            _calls++;
            return true;
        }
    }

    /// <summary>The marker of <paramref name="hit"/>: the one it already has, or the next free one (<c>S7</c> after six sources).</summary>
    public (string Ref, bool Added) AddSource(SearchHit hit)
    {
        lock (_gate)
        {
            if (_sources.FirstOrDefault(s => s.ChunkId == hit.ChunkId) is { } known)
            {
                return (known.Ref, false);
            }

            var reference = $"S{_sources.Count + 1}";
            _sources.Add(new SourceRef(reference, hit.ChunkId, hit.Type, hit.Title, hit.SectionPath, hit.Reference));
            return (reference, true);
        }
    }

    public void Record(string callId, ToolOutcome outcome)
    {
        lock (_gate)
        {
            _outcomes[callId] = outcome;
            _lastCallId = callId;
        }
    }

    /// <summary>The call ID of the last recorded call (a call made outside function invocation gets a generated one).</summary>
    public string LastCallId()
    {
        lock (_gate)
        {
            return _lastCallId ?? throw new InvalidOperationException("No tool call was recorded.");
        }
    }

    /// <summary>The outcome of the call (and forgets it), or null when the tool never ran (the model's arguments did not bind, say).</summary>
    public ToolOutcome? TakeOutcome(string callId)
    {
        lock (_gate)
        {
            return _outcomes.Remove(callId, out var outcome) ? outcome : null;
        }
    }
}
