using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Assistant.Application.Guardrails;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// Wraps the service's embedding generator (the Fake provider's pipeline): records every call, and lets a test make calls whose texts
/// contain a marker fail (<see cref="HttpRequestException"/>, a provider outage) or wait until the test releases them. Tests pick
/// markers of their own, so the shared app (and the seeded documents it ingests meanwhile) do not disturb them.
/// </summary>
public sealed class EmbeddingProbe
{
    private readonly ConcurrentQueue<string[]> _calls = new();
    private readonly ConcurrentDictionary<string, bool> _failing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<Exception>> _throwing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Hold> _holds = new(StringComparer.Ordinal);

    /// <summary>The texts of each call, in call order.</summary>
    public IReadOnlyList<string[]> Calls => [.. _calls];

    /// <summary>The calls with a text containing <paramref name="marker"/>.</summary>
    public int CallsWith(string marker) => Calls.Count(c => c.Any(t => t.Contains(marker, StringComparison.Ordinal)));

    /// <summary>The texts containing <paramref name="marker"/> that were embedded (or tried).</summary>
    public IReadOnlyList<string> TextsWith(string marker) =>
        [.. Calls.SelectMany(c => c).Where(t => t.Contains(marker, StringComparison.Ordinal))];

    public void FailWhenContains(string marker) => _failing[marker] = true;

    /// <summary>Calls with <paramref name="marker"/> throw what <paramref name="exception"/> makes (an unexpected failure, a bug).</summary>
    public void ThrowWhenContains(string marker, Func<Exception> exception) => _throwing[marker] = exception;

    /// <summary>Calls with <paramref name="marker"/> wait until <see cref="Hold.Release"/>; <see cref="Hold.Entered"/> completes when one arrives.</summary>
    public Hold HoldWhenContains(string marker) => _holds[marker] = new Hold();

    internal async Task BeforeAsync(string[] texts, CancellationToken cancellationToken)
    {
        _calls.Enqueue(texts);
        foreach (var (marker, hold) in _holds)
        {
            if (texts.Any(t => t.Contains(marker, StringComparison.Ordinal)))
            {
                hold.Enter();
                await hold.WaitAsync(cancellationToken);
            }
        }

        foreach (var (marker, exception) in _throwing)
        {
            if (texts.Any(t => t.Contains(marker, StringComparison.Ordinal)))
            {
                throw exception();
            }
        }

        if (_failing.Keys.Any(marker => texts.Any(t => t.Contains(marker, StringComparison.Ordinal))))
        {
            throw new HttpRequestException("The test provider is down.", null, System.Net.HttpStatusCode.ServiceUnavailable);
        }
    }

    public sealed class Hold
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        internal void Enter() => _entered.TrySetResult();

        internal Task WaitAsync(CancellationToken cancellationToken) => _released.Task.WaitAsync(cancellationToken);
    }
}

internal sealed class ProbeEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, EmbeddingProbe probe)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var texts = values.ToArray();
        await probe.BeforeAsync(texts, cancellationToken);
        return await base.GenerateAsync(texts, options, cancellationToken);
    }
}

/// <summary>
/// The document guard of the tests (ledger ruling A): blocks a document containing <see cref="Marker"/> with <see cref="Reason"/>. Plan 05
/// Task 4 brings the real guard and completes the poisoned-document test with it.
/// </summary>
public sealed class MarkerPromptShield : IPromptShield
{
    public const string Marker = "#test-poison-marker#";
    public const string Reason = "The test guard found instructions to the assistant.";

    public Task<PromptShieldResult> CheckQuestionAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(PromptShieldResult.Allowed);

    public Task<PromptShieldResult> CheckDocumentAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(text.Contains(Marker, StringComparison.Ordinal) ? PromptShieldResult.Block(Reason) : PromptShieldResult.Allowed);
}

internal static class TestServiceCollectionExtensions
{
    /// <summary>Replaces the last registration of <typeparamref name="T"/> by <paramref name="decorate"/> of what it built.</summary>
    public static void Decorate<T>(this IServiceCollection services, Func<T, IServiceProvider, T> decorate)
        where T : class
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(T) && !d.IsKeyedService);
        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(T),
            sp => decorate(
                descriptor.ImplementationInstance as T
                ?? (descriptor.ImplementationFactory is { } factory
                    ? (T)factory(sp)
                    : (T)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!)),
                sp),
            descriptor.Lifetime));
    }
}
