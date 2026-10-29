using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Application.Retrieval.PastPurchases;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Requests;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// The past purchase error rules are set on the three handler chains only, so the policy document commands and the ingestion job keep the
/// service-wide rules (and ingestion its own "last attempt ends the version Failed" logic); on those chains the model provider rule is
/// checked first.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class PastPurchaseRulesTests(AssistantApp app)
{
    private static readonly Type[] PurchaseMessages = [typeof(RequestSubmitted), typeof(RequestApproved), typeof(RequestCompleted)];

    private static readonly Exception LostRace = new DbUpdateConcurrencyException("lost race");

    private static readonly Exception DuplicateInsert =
        new DbUpdateException("insert", new PostgresException("duplicate key", "ERROR", "ERROR", "23505"));

    private static readonly Exception MissingData = new PurchaseDataMissingException(Guid.NewGuid());

    private static readonly Exception ProviderDown = new AiProviderUnavailableException(new HttpRequestException("down"));

    [Fact]
    public void Purchase_retry_rules_are_set_on_the_purchase_chains_only()
    {
        var runtime = app.Services.GetRequiredService<IWolverineRuntime>();
        var serviceWide = ((IWithFailurePolicies)runtime.Options).Failures;
        var graph = app.Services.GetRequiredService<Wolverine.Runtime.Handlers.HandlerGraph>();

        // Service-wide: none of the purchase rules (the model provider rule of ingestion stays).
        Matching(serviceWide, LostRace).ShouldBeEmpty();
        Matching(serviceWide, DuplicateInsert).ShouldBeEmpty();
        Matching(serviceWide, MissingData).ShouldBeEmpty();

        foreach (var type in PurchaseMessages)
        {
            var chain = graph.ChainFor(type).ShouldNotBeNull(type.Name);
            Matching(chain.Failures, LostRace).ShouldNotBeEmpty(type.Name);
            Matching(chain.Failures, DuplicateInsert).ShouldNotBeEmpty(type.Name);
            Matching(chain.Failures, MissingData).ShouldNotBeEmpty(type.Name);
            // A wrapped provider failure also looks like a failed call to another service: the provider rule must come first.
            Matching(chain.Failures, ProviderDown).First().Match.Description.ShouldContain(nameof(AiProviderUnavailableException));
        }

        foreach (var type in new[] { typeof(UploadPolicyDocument), typeof(IngestPolicyDocument) })
        {
            var chain = graph.ChainFor(type).ShouldNotBeNull(type.Name);
            Matching(chain.Failures, LostRace).ShouldBeEmpty(type.Name);
            Matching(chain.Failures, MissingData).ShouldBeEmpty(type.Name);
        }
    }

    private static List<FailureRule> Matching(FailureRuleCollection rules, Exception exception) =>
        [.. rules.Where(rule => rule.Match.Matches(exception))];
}
