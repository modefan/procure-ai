using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.Audit.Infrastructure;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Requests;
using Shouldly;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>The business audit: every integration event of the contracts is stored once, attributed to the user of the message headers.</summary>
[Collection(AuditCollection.Name)]
public sealed class BusinessAuditTests(AuditApp app)
{
    private const string Tom = DemoUsers.Tom;
    private const string Maria = DemoUsers.Maria;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every integration event type of ProcureAi.Contracts, by reflection: a new event is in this list without anyone adding it.</summary>
    private static readonly Type[] EventTypes = typeof(IIntegrationEvent).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IIntegrationEvent).IsAssignableFrom(t))
        .ToArray();

    /// <summary>
    /// The exchange a service publishes an event to: one exchange per publishing service, named like the contract's namespace
    /// (<c>ProcureAi.Contracts.Approvals</c> → <c>approvals</c>), as the services' Wolverine routes do.
    /// </summary>
    private static string ExchangeOf(Type type) => type.Namespace![(type.Namespace!.LastIndexOf('.') + 1)..].ToLowerInvariant();

    [Fact]
    public async Task Every_integration_event_type_is_recorded()
    {
        EventTypes.Length.ShouldBeGreaterThan(10, "the contracts were found by reflection");
        var tenant = AuditApp.NewTenant();
        var requestId = Guid.NewGuid();

        foreach (var type in EventTypes)
        {
            var exchange = ExchangeOf(type);
            DependencyInjection.AuditedExchanges.ShouldContain(exchange, $"{type.Name} is published to '{exchange}', which Audit must listen to");
            await app.PublishAsync(Sample(type, tenant, requestId), exchange, tenant, Tom);
        }

        var stored = await app.WaitForStoredAsync(tenant, s => s.Count >= EventTypes.Length);

        stored.Select(e => e.Type).ShouldBe(EventTypes.Select(t => t.Name), ignoreOrder: true);
        stored.ShouldAllBe(e => e.UserId == Tom && e.TenantId == tenant);
        stored.Where(e => e.Type != nameof(ProcureAi.Contracts.Access.AccessChanged)).ShouldAllBe(e => e.SubjectId == requestId);
    }

    [Fact]
    public async Task Duplicate_delivery_records_once()
    {
        var tenant = AuditApp.NewTenant();
        var probe = app.Probe;
        var captured = await probe.BindQueueAsync(Exchanges.Requests);
        var submitted = Submitted(Guid.NewGuid(), tenant);

        await app.PublishAsync(submitted, Exchanges.Requests, tenant, Tom);
        var raw = await probe.WaitForRawAsync(captured, AuditApp.Timeout);
        var messageId = Guid.Parse(raw.MessageId.ShouldNotBeNull());
        var first = (await app.WaitForStoredAsync(tenant, s => s.Count == 1)).Single();

        // The document ID is the message ID the publisher gave the event, the same on every delivery.
        first.Id.ShouldBe(messageId);

        // Redelivered while the durable inbox still knows the message, and again after the inbox forgot it (retention):
        // then only the document ID stands between the redelivery and a second record.
        await probe.RepublishAsync(Exchanges.Requests, raw);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await app.ForgetInboxAsync(messageId);
        await probe.RepublishAsync(Exchanges.Requests, raw);
        await EventuallyAsync(async () => await app.InboxStatusAsync(messageId) == "Handled");

        var stored = await app.StoredAsync(tenant);
        stored.ShouldHaveSingleItem().ShouldBe(first);
    }

    [Fact]
    public async Task History_is_tenant_scoped_404_for_other_tenant_request()
    {
        var tenant = AuditApp.NewTenant();
        var other = AuditApp.NewTenant();
        var requestId = Guid.NewGuid();
        await app.PublishAsync(Submitted(requestId, tenant), Exchanges.Requests, tenant, Tom);
        await app.WaitForStoredAsync(tenant, s => s.Count == 1);

        using var own = await app.Client.SendAsync(AuditApp.Get(History(requestId), TestAuth.CreateToken(Tom, tenant, "requester")), Ct);
        using var asOther = await app.Client.SendAsync(AuditApp.Get(History(requestId), TestAuth.CreateToken(Tom, other, "requester")), Ct);
        using var unknown = await app.Client.SendAsync(AuditApp.Get(History(Guid.NewGuid()), TestAuth.CreateToken(Tom, tenant, "requester")), Ct);

        own.StatusCode.ShouldBe(HttpStatusCode.OK, await own.Content.ReadAsStringAsync(Ct));
        (await own.Content.ReadFromJsonAsync<List<BusinessEventDto>>(Ct)).ShouldNotBeNull().ShouldHaveSingleItem().Type.ShouldBe("RequestSubmitted");
        asOther.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another tenant's request looks like no request");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task History_shows_user_from_header_not_actor()
    {
        var tenant = AuditApp.NewTenant();
        var requestId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        app.Access.SetDisplayName(tenant, Tom, "Tom Requester");
        app.Access.SetDisplayName(tenant, Maria, "Maria Manager");

        await app.PublishAsync(Submitted(requestId, tenant, now.AddMinutes(-3)), Exchanges.Requests, tenant, Tom);
        await app.PublishAsync(
            new ApprovalRequested(requestId, tenant, "PR-1", stepId, 1, "Manager", [Maria], now.AddDays(2), now.AddMinutes(-2)),
            Exchanges.Approvals, tenant, Tom);
        // The saga publishes the approval while acting as service:approvals-api for Maria: Maria is in the headers.
        await app.PublishAsync(new ApprovalStepApproved(requestId, tenant, stepId, Maria, now.AddMinutes(-1)), Exchanges.Approvals, tenant, Maria);
        await app.WaitForStoredAsync(tenant, s => s.Count == 3);

        using var response = await app.Client.SendAsync(AuditApp.Get(History(requestId), TestAuth.CreateToken(Tom, tenant, "requester")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var history = (await response.Content.ReadFromJsonAsync<List<BusinessEventDto>>(Ct)).ShouldNotBeNull();
        history.Select(h => h.Type).ShouldBe(["RequestSubmitted", "ApprovalRequested", "ApprovalStepApproved"], "oldest first");
        history[0].UserId.ShouldBe(Tom);
        history[0].Summary.ShouldBe("Submitted by Tom Requester: Laptop (R 30 000,00 incl. VAT, IT-200)");
        history[1].Summary.ShouldBe("Approval requested from Maria Manager (step 1, Manager)");
        history[2].UserId.ShouldBe(Maria, "the initiating user of the headers, not the service that published it");
        history[2].Summary.ShouldBe("Approved by Maria Manager (step 1, Manager)");
    }

    [Fact]
    public async Task History_answers_with_user_ids_when_access_is_down()
    {
        var tenant = AuditApp.NewTenant();
        var requestId = Guid.NewGuid();
        app.Access.SetDisplayName(tenant, Tom, "Tom Requester");
        await app.PublishAsync(Submitted(requestId, tenant), Exchanges.Requests, tenant, Tom);
        await app.WaitForStoredAsync(tenant, s => s.Count == 1);

        app.Access.Down = true;
        try
        {
            // The admin key needs no permissions from Access, so only the display names are missing.
            using var response = await app.Client.SendAsync(AuditApp.GetWithApiKey(History(requestId), tenant), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var history = (await response.Content.ReadFromJsonAsync<List<BusinessEventDto>>(Ct)).ShouldNotBeNull();
            history.ShouldHaveSingleItem().Summary.ShouldStartWith($"Submitted by {Tom}:");
        }
        finally
        {
            app.Access.Down = false;
        }
    }

    [Fact]
    public async Task AccessChanged_is_recorded_and_evicts_cached_display_names()
    {
        var tenant = AuditApp.NewTenant();
        var requestId = Guid.NewGuid();
        app.Access.SetDisplayName(tenant, Tom, "Tom Before");
        await app.PublishAsync(Submitted(requestId, tenant), Exchanges.Requests, tenant, Tom);
        await app.WaitForStoredAsync(tenant, s => s.Count == 1);
        (await HistoryAsync(requestId, tenant)).Single().Summary.ShouldStartWith("Submitted by Tom Before:");

        app.Access.SetDisplayName(tenant, Tom, "Tom After");
        await app.PublishAsync(new ProcureAi.Contracts.Access.AccessChanged(tenant, "User", Tom, DateTimeOffset.UtcNow), Exchanges.Access, tenant, DemoUsers.Admin);
        await app.WaitForStoredAsync(tenant, s => s.Any(e => e.Type == "AccessChanged"));

        // The shared AccessChangedHandler ran on the same delivery: the name cached for 5 minutes is gone (after the 1 s read cache).
        await EventuallyAsync(async () => (await HistoryAsync(requestId, tenant)).Single().Summary.StartsWith("Submitted by Tom After:", StringComparison.Ordinal));
    }

    private async Task<List<BusinessEventDto>> HistoryAsync(Guid requestId, string tenant)
    {
        using var response = await app.Client.SendAsync(AuditApp.GetWithApiKey(History(requestId), tenant), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<List<BusinessEventDto>>(Ct)).ShouldNotBeNull();
    }

    private static string History(Guid requestId) => $"/api/v1/audit/requests/{requestId}/history";

    private static RequestSubmitted Submitted(Guid requestId, string tenant, DateTimeOffset? at = null) =>
        new(requestId, tenant, "PR-1", "Laptop", Tom, "IT-200", 30_000m, at ?? DateTimeOffset.UtcNow);

    /// <summary>An instance of any contract: the tenant and request of the test, plausible values elsewhere. A new parameter type fails here, loudly.</summary>
    private static object Sample(Type type, string tenant, Guid requestId)
    {
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var arguments = constructor.GetParameters().Select(p => Value(p, tenant, requestId)).ToArray();
        return constructor.Invoke(arguments);
    }

    private static object? Value(ParameterInfo parameter, string tenant, Guid requestId) => parameter switch
    {
        { Name: "TenantId" } => tenant,
        { Name: "RequestId" } => requestId,
        _ when parameter.ParameterType == typeof(string) => $"{parameter.Name}-sample",
        _ when parameter.ParameterType == typeof(Guid) => Guid.NewGuid(),
        _ when parameter.ParameterType == typeof(decimal) => 1_000m,
        _ when parameter.ParameterType == typeof(int) => 1,
        _ when parameter.ParameterType == typeof(DateTimeOffset) => DateTimeOffset.UtcNow,
        _ when parameter.ParameterType == typeof(IReadOnlyList<string>) => new[] { Maria },
        _ => throw new InvalidOperationException($"No sample value for {parameter.Member.DeclaringType!.Name}.{parameter.Name} ({parameter.ParameterType})."),
    };

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + AuditApp.Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not hold in time.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }
}
