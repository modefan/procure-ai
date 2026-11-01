using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using ProcureAi.Audit.Application;
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
            await app.PublishAsync(ContractSamples.Of(type, tenant, requestId, Maria), exchange, tenant, Tom);
        }

        var stored = await app.WaitForStoredAsync(tenant, s => s.Count >= EventTypes.Length);

        stored.Select(e => e.Type).ShouldBe(EventTypes.Select(t => t.Name), ignoreOrder: true);
        stored.ShouldAllBe(e => e.UserId == Tom && e.TenantId == tenant);
        // Access changes, budget amount changes and policy document events belong to no purchase request.
        string[] withoutRequest =
        [
            nameof(ProcureAi.Contracts.Access.AccessChanged), nameof(ProcureAi.Contracts.Budgets.BudgetAmountSet),
            nameof(ProcureAi.Contracts.Assistant.PolicyDocumentProgressed), nameof(ProcureAi.Contracts.Assistant.PolicyDocumentStatusChanged),
        ];
        stored.Where(e => !withoutRequest.Contains(e.Type)).ShouldAllBe(e => e.SubjectId == requestId);
        stored.Where(e => withoutRequest.Contains(e.Type) && e.Type != nameof(ProcureAi.Contracts.Access.AccessChanged))
            .ShouldAllBe(e => e.SubjectId == null);
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
    public async Task History_shows_the_approve_comment()
    {
        var tenant = AuditApp.NewTenant();
        var requestId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        app.Access.SetDisplayName(tenant, Tom, "Tom Mokoena");
        app.Access.SetDisplayName(tenant, Maria, "Maria Naidoo");
        await app.PublishAsync(Submitted(requestId, tenant, now.AddMinutes(-3)), Exchanges.Requests, tenant, Tom);
        await app.PublishAsync(
            new ApprovalRequested(requestId, tenant, "PR-1", stepId, 1, "Manager", [Maria], now.AddDays(2), now.AddMinutes(-2)),
            Exchanges.Approvals, tenant, Tom);
        await app.PublishAsync(new ApprovalStepApproved(requestId, tenant, stepId, Maria, now.AddMinutes(-1), "Within budget"), Exchanges.Approvals, tenant, Maria);
        await app.WaitForStoredAsync(tenant, s => s.Count == 3);

        using var response = await app.Client.SendAsync(AuditApp.Get(History(requestId), TestAuth.CreateToken(Tom, tenant, "requester")), Ct);

        var history = (await response.Content.ReadFromJsonAsync<List<BusinessEventDto>>(Ct)).ShouldNotBeNull();
        history[2].Summary.ShouldBe("Approved by Maria Naidoo (step 1, Manager): Within budget");
    }

    [Fact]
    public async Task History_shows_user_from_header_not_actor()
    {
        var tenant = AuditApp.NewTenant();
        var requestId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        app.Access.SetDisplayName(tenant, Tom, "Tom Mokoena");
        app.Access.SetDisplayName(tenant, Maria, "Maria Naidoo");

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
        history[0].Summary.ShouldBe("Submitted by Tom Mokoena: Laptop (R 30 000,00 incl. VAT, IT-200)");
        history[1].Summary.ShouldBe("Approval requested from Maria Naidoo (step 1, Manager)");
        history[2].UserId.ShouldBe(Maria, "the initiating user of the headers, not the service that published it");
        history[2].Summary.ShouldBe("Approved by Maria Naidoo (step 1, Manager)");

        // The request detail page reads these instead of parsing the sentences above (workflow-timeline.ts).
        history.Select(h => h.StepNo).ShouldBe([null, 1, 1], "the approval events say which step they are about");
        history.Select(h => h.ActorName).ShouldBe(["Tom Mokoena", "Tom Mokoena", "Maria Naidoo"], "the display name of the user in the headers");
    }

    [Fact]
    public async Task History_answers_with_user_ids_when_access_is_down()
    {
        var tenant = AuditApp.NewTenant();
        var requestId = Guid.NewGuid();
        app.Access.SetDisplayName(tenant, Tom, "Tom Mokoena");
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
    public async Task History_answers_with_user_ids_within_the_timeout_when_access_is_slow()
    {
        var tenant = AuditApp.NewTenant();
        var requestId = Guid.NewGuid();
        var timeout = app.Services.GetRequiredService<IOptions<AuditOptions>>().Value.DisplayNameTimeout;
        app.Access.SetDisplayName(tenant, Tom, "Tom Mokoena");
        app.Access.SetNameDelay(tenant, TimeSpan.FromSeconds(20));
        await app.PublishAsync(Submitted(requestId, tenant), Exchanges.Requests, tenant, Tom);
        await app.WaitForStoredAsync(tenant, s => s.Count == 1);

        var clock = Stopwatch.StartNew();
        using var response = await app.Client.SendAsync(AuditApp.GetWithApiKey(History(requestId), tenant), Ct);
        clock.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<List<BusinessEventDto>>(Ct)).ShouldNotBeNull()
            .ShouldHaveSingleItem().Summary.ShouldStartWith($"Submitted by {Tom}:");
        // About the timeout (2 s), far from Access's 20 s; the margin covers the query and a busy test machine.
        clock.Elapsed.ShouldBeLessThan(timeout + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Business_events_are_indexed_by_tenant_and_subject_together()
    {
        await using var connection = new NpgsqlConnection(app.Infra.DbConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'audit' AND tablename = 'mt_doc_businessevent'", connection);
        var indexes = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                indexes.Add(reader.GetString(0));
            }
        }

        // One index for the history query (tenant and request), not one per column.
        indexes.ShouldContain(i => i.Contains("tenantid", StringComparison.OrdinalIgnoreCase) && i.Contains("subjectid", StringComparison.OrdinalIgnoreCase),
            string.Join(Environment.NewLine, indexes));
        indexes.ShouldNotContain(i => i.Contains("tenantid", StringComparison.OrdinalIgnoreCase) != i.Contains("subjectid", StringComparison.OrdinalIgnoreCase),
            string.Join(Environment.NewLine, indexes));
    }

    [Fact]
    public void Audit_options_come_from_configuration_only()
    {
        // Nothing in code: options that were never bound are not valid, so a missing appsettings value fails at startup.
        var unbound = new AuditOptions();
        Validator.TryValidateObject(unbound, new ValidationContext(unbound), [], validateAllProperties: true).ShouldBeFalse();

        var bound = app.Services.GetRequiredService<IOptions<AuditOptions>>().Value;
        bound.DisplayNameTimeout.ShouldBe(TimeSpan.FromSeconds(2));
        bound.Matching.Window.ShouldBe(TimeSpan.FromMinutes(10));
        bound.Matching.Grace.ShouldBe(AuditApiFactory.Grace, "the tests' setting overrides appsettings' 30 s");
        bound.RetentionDays.ShouldBe(90);
        bound.PurgeInterval.ShouldBe(TimeSpan.FromDays(1));
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

    [Fact]
    public async Task A_redis_failure_while_handling_AccessChanged_is_retried_until_the_generation_is_stored()
    {
        // Audit records AccessChanged and advances the Access cache generation in one chain. A Redis blip in the second must
        // retry the chain, not send the message to the error queue: otherwise the other instances keep the old generation.
        // The record itself is stored once either way (its document ID is the message ID).
        var tenant = AuditApp.NewTenant();
        app.GenerationFaults.FailNextWriteOf(tenant);

        await app.PublishAsync(new ProcureAi.Contracts.Access.AccessChanged(tenant, "Matrix", null, DateTimeOffset.UtcNow), Exchanges.Access, tenant, DemoUsers.Admin);

        (await app.WaitForStoredAsync(tenant, s => s.Any(e => e.Type == "AccessChanged"))).Count(e => e.Type == "AccessChanged").ShouldBe(1);
        await EventuallyAsync(() => Task.FromResult(app.GenerationFaults.Written(tenant)));
        app.GenerationFaults.Failed.ShouldBeGreaterThanOrEqualTo(1);
        (await app.StoredAsync(tenant)).Count(e => e.Type == "AccessChanged").ShouldBe(1);
    }

    private async Task<List<BusinessEventDto>> HistoryAsync(Guid requestId, string tenant)
    {
        using var response = await app.Client.SendAsync(AuditApp.GetWithApiKey(History(requestId), tenant), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<List<BusinessEventDto>>(Ct)).ShouldNotBeNull();
    }

    private static string History(Guid requestId) => $"/api/v1/audit/requests/{requestId}/history";

    private static RequestSubmitted Submitted(Guid requestId, string tenant, DateTimeOffset? at = null) =>
        new(requestId, tenant, "PR-1", "Laptop", Tom, "IT-200", 30_000m, at ?? DateTimeOffset.UtcNow, "SUP-1", []);

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
