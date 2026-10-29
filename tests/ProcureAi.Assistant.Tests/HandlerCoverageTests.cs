using ProcureAi.Assistant.Application.Retrieval.PastPurchases;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Requests;
using ProcureAi.Testing;
using Shouldly;

namespace ProcureAi.Assistant.Tests;

public sealed class HandlerCoverageTests
{
    /// <summary>
    /// The queue <c>assistant-api-requests</c> is bound to the <c>requests</c> and <c>approvals</c> exchanges, but the Assistant needs only
    /// three of their events (ruling 9). The others are discarded by Wolverine as having no handler (logged, never dead-lettered; see
    /// <c>Rejected_request_is_not_indexed</c>), so this checks an include list rather than every event of the two namespaces.
    /// </summary>
    [Fact]
    public void The_three_past_purchase_events_have_handlers() =>
        HandlerCoverage.AssertEventsHandled(
            typeof(RequestSubmittedHandler).Assembly, typeof(RequestSubmitted), typeof(RequestApproved), typeof(RequestCompleted));

    [Fact]
    public void A_missing_handler_is_reported() =>
        Should.Throw<InvalidOperationException>(() =>
                HandlerCoverage.AssertEventsHandled(typeof(RequestSubmittedHandler).Assembly, typeof(RequestRejected)))
            .Message.ShouldContain(typeof(RequestRejected).FullName!);
}
