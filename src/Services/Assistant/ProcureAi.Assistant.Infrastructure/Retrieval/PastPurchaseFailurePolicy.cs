using JasperFx;
using JasperFx.CodeGeneration;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Application.Retrieval.PastPurchases;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Requests;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace ProcureAi.Assistant.Infrastructure.Retrieval;

/// <summary>
/// The error rules of the three past purchase handlers, set on their chains only, so the policy document commands and the ingestion job
/// keep their own behaviour. A chain's rules are checked before the service-wide ones, so the order here is the order that counts:
/// <list type="number">
/// <item>a model provider failure (<see cref="AiProviderUnavailableException"/>, which wraps an <see cref="HttpRequestException"/>) is
/// retried on the ingestion schedule, before the dependency rule below could take it for an Access failure;</item>
/// <item>a completion before its submit data (<see cref="PurchaseDataMissingException"/>) on <c>Assistant:Purchases:RetryDelays</c>;</item>
/// <item>a lost race on the purchase row (row version, or a concurrent insert of the same request: unique violation) inline
/// (<c>Assistant:Purchases:ConcurrencyRetry</c>);</item>
/// <item>a failed Access lookup of the approvers' names on a schedule (<c>Assistant:Purchases:DependencyRetry</c>).</item>
/// </list>
/// Each ends in the error queue after its last retry.
/// </summary>
internal sealed class PastPurchaseFailurePolicy(PastPurchasesOptions purchases, TimeSpan[] providerDelays) : IHandlerPolicy
{
    public static readonly Type[] Messages = [typeof(RequestSubmitted), typeof(RequestApproved), typeof(RequestCompleted)];

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var missingData = purchases.RetryDelaySchedule();
        foreach (var chain in chains.Where(c => Messages.Contains(c.MessageType)))
        {
            chain.OnException<AiProviderUnavailableException>().ScheduleRetry(providerDelays).Then.MoveToErrorQueue();
            chain.OnException<PurchaseDataMissingException>().ScheduleRetry(missingData).Then.MoveToErrorQueue();
            chain.RetryConcurrencyConflicts(
                purchases.ConcurrencyRetry, $"{PastPurchasesOptions.SectionPath}:{nameof(PastPurchasesOptions.ConcurrencyRetry)}");
            chain.RetryTransientDependencyFailures(
                purchases.DependencyRetry, $"{PastPurchasesOptions.SectionPath}:{nameof(PastPurchasesOptions.DependencyRetry)}");
        }
    }
}
