using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Configuration;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Contracts.Access;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>
/// Retries the handling of <c>AccessChanged</c> when the new cache generation could not be stored in Redis or announced on the
/// backplane (<see cref="AccessChangedHandler"/> throws then). Without it, Wolverine's default for an exception no rule matches is
/// the error queue on the first attempt, and a Redis blip would leave every other instance on the old generation, still
/// granting what was just revoked. Retries are scheduled (durable inbox, the listener moves on) with the delays of the
/// <see cref="SectionName"/> section, and the message goes to the error queue after the last one. The handler is idempotent
/// (each attempt advances to a new generation), and the rule is set on the chain of <c>AccessChanged</c> itself, so it also
/// covers a chain that does more with the message (Audit records it in the same chain).
/// </summary>
public static class AccessChangedRetryPolicy
{
    public const string SectionName = "Messaging:AccessChangedRetry";

    /// <summary>The defaults (1 s, 5 s, 25 s, about 2 min, 10 min); a service can override any of them in <see cref="SectionName"/>.</summary>
    public static DependencyRetryOptions Defaults() => new()
    {
        Attempts = 5,
        InitialDelay = TimeSpan.FromSeconds(1),
        Multiplier = 5.0,
        MaxDelay = TimeSpan.FromMinutes(10),
    };

    /// <summary>The configured delays, validated (an invalid section fails at startup).</summary>
    public static TimeSpan[] Delays(IConfiguration configuration)
    {
        var retry = Defaults();
        configuration.GetSection(SectionName).Bind(retry);
        return retry.Delays(SectionName);
    }

    /// <summary>Adds the retry rule to the chain that handles <c>AccessChanged</c>, if this service has one.</summary>
    public static WolverineOptions RetryAccessChangedCacheFailures(this WolverineOptions options, TimeSpan[] delays)
    {
        options.Policies.Add(new Policy(delays));
        return options;
    }

    /// <summary>True when the cache generation could not be written to Redis or announced on the backplane.</summary>
    public static bool IsCacheFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FusionCacheDistributedCacheException or FusionCacheBackplaneException)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Policy(TimeSpan[] delays) : IHandlerPolicy
    {
        public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
        {
            foreach (var chain in chains.Where(c => c.MessageType == typeof(AccessChanged)))
            {
                chain.OnException(IsCacheFailure, "the Access cache generation could not be stored")
                    .ScheduleRetry(delays)
                    .Then.MoveToErrorQueue();
            }
        }
    }
}
