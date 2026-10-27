using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Domain;
using ProcureAi.Requests.Infrastructure;
using Shouldly;
using Wolverine;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// Requests follows the approval saga: the pre-check against Approvals before a submission, the outcome events
/// (RequestApproved / RequestRejected) from the <c>approvals</c> exchange, and the requester's cancel (RequestCancelled to the
/// <c>requests</c> exchange). Real Program, Postgres, RabbitMQ and Redis; a stub Approvals and Access.
/// </summary>
[Collection(InfraCollection.Name)]
public sealed class SagaFollowTests(InfraFixture infra) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(30);

    private const string Requests = "/api/v1/requests";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(infra);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string NewTenant(string name) => $"{name}-{Guid.NewGuid():N}"[..24];

    private static HttpRequestMessage Req(
        HttpMethod method, string url, string tenant, object? body = null, string? bearer = null, string? ifMatch = null,
        string? onBehalfOf = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        else
        {
            request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
            request.Headers.Add("X-Tenant-Id", tenant);
            if (onBehalfOf is not null)
            {
                request.Headers.Add("X-On-Behalf-Of-User", onBehalfOf);
            }
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (method == HttpMethod.Post)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return request;
    }

    private static string ETagOf(RequestDto dto) => $"\"{dto.Version}\"";

    private async Task<RequestDto> CreateAsync(string tenant, string? bearer = null)
    {
        var body = new
        {
            title = "Laptops",
            costCenterCode = "CC-100",
            supplierId = "sup-1",
            lines = new[] { new { sku = "LAP-1", description = "Laptop", quantity = 3, unitPriceExclVat = 25200m } },
        };
        var response = await _client.SendAsync(Req(HttpMethod.Post, Requests, tenant, body, bearer), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    private async Task<RequestDto> SubmitAsync(string tenant, RequestDto created)
    {
        var response = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{created.Id}/submit", tenant, ifMatch: ETagOf(created)), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    private async Task<RequestDto> GetAsync(HttpClient client, string tenant, Guid id)
    {
        var response = await client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{id}", tenant), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    /// <summary>Reads until <paramref name="done"/> holds; fails with the last state seen.</summary>
    private async Task<RequestDto> WaitForAsync(HttpClient client, string tenant, Guid id, Func<RequestDto, bool> done)
    {
        var deadline = DateTime.UtcNow + MessageTimeout;
        while (true)
        {
            var current = await GetAsync(client, tenant, id);
            if (done(current))
            {
                return current;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Request {id} is still {current.Status} after {MessageTimeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    /// <summary>Publishes to the <c>approvals</c> exchange through the broker, as the approval saga does, with the caller headers it carries.</summary>
    private static Task PublishFromApprovalsAsync(WebApplicationFactory<Program> factory, object message, string tenant, string user) =>
        factory.Services.GetRequiredService<IMessageBus>()
            .EndpointFor(new Uri($"rabbitmq://exchange/{Exchanges.Approvals}"))
            .SendAsync(message, new DeliveryOptions().WithHeader(CallerHeaders.TenantId, tenant).WithHeader(CallerHeaders.UserId, user))
            .AsTask();

    private static RequestApproved Approved(RequestDto request, string tenant, string approver = DemoUsers.Maria) =>
        new(request.Id, tenant, request.Number, request.TotalInclVat, [approver], DateTimeOffset.UtcNow);

    private static RequestRejected Rejected(RequestDto request, string tenant, string reason, string? by = DemoUsers.Maria) =>
        new(request.Id, tenant, request.Number, reason, by, DateTimeOffset.UtcNow);

    // ---- The pre-check before a submission --------------------------------------------------------------------------------

    [Fact]
    public async Task Submit_refused_422_when_no_eligible_approver()
    {
        var tenant = NewTenant("noappr");
        infra.Approvals.SetNotPossible(tenant, "No eligible approver for CC-100: the manager raised the request");
        var created = await CreateAsync(tenant);
        await using var recorder = await RabbitRecorder.StartAsync(infra.RabbitConnectionString, [Exchanges.Requests], []);

        var response = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{created.Id}/submit", tenant, ifMatch: ETagOf(created)), Ct);

        response.StatusCode.ShouldBe((HttpStatusCode)422, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("title").GetString()!.ShouldContain("No eligible approver for CC-100");
        (await GetAsync(_client, tenant, created.Id)).Status.ShouldBe(RequestStatus.Draft);
        // Approvals was asked for this request: its cost center, total incl. VAT and requester, as Requests itself.
        var call = infra.Approvals.Calls.Where(c => c.Tenant == tenant).ShouldHaveSingleItem();
        call.CostCenter.ShouldBe("CC-100");
        call.Amount.ShouldBe(86940m);
        call.RequesterId.ShouldBe("claude-admin");
        call.CallingService.ShouldBe("requests-api");
        call.HasBearer.ShouldBeTrue();
        // Nothing reached the saga.
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        recorder.Of(created.Id).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_503_with_retry_after_when_approvals_down(bool unreachable)
    {
        var tenant = NewTenant("apprdown");
        infra.Approvals.SetDown(tenant);
        // Unreachable: nothing listens at the address (connection refused), rather than Approvals answering 503.
        await using var factory = unreachable
            ? new ApiFactory(infra, settings: new Dictionary<string, string?> { ["Services:approvals-api:http:0"] = "http://127.0.0.1:9" })
            : null;
        using var client = factory?.CreateClient() ?? _factory.CreateClient();
        var created = await CreateAsync(tenant);

        var response = await client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{created.Id}/submit", tenant, ifMatch: ETagOf(created)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.RetryAfter.ShouldNotBeNull();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await GetAsync(client, tenant, created.Id)).Status.ShouldBe(RequestStatus.Draft);
    }

    // ---- Outcome events from the saga -------------------------------------------------------------------------------------

    [Fact]
    public async Task RequestApproved_message_sets_status_and_fresh_get_within_1s()
    {
        // A 60 s GET cache: only an eviction, not expiry, can make the next read fresh.
        await using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Caching:DefaultGetSeconds", "60"));
        using var client = factory.CreateClient();
        var tenant = NewTenant("approved");
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));
        (await GetAsync(client, tenant, submitted.Id)).Status.ShouldBe(RequestStatus.Submitted);
        var listBefore = await client.SendAsync(Req(HttpMethod.Get, $"{Requests}?status=Submitted", tenant), Ct);
        (await listBefore.Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct))!.Total.ShouldBe(1);

        await PublishFromApprovalsAsync(factory, Approved(submitted, tenant), tenant, DemoUsers.Maria);

        var approved = await WaitForAsync(client, tenant, submitted.Id, r => r.Status == RequestStatus.Approved);
        approved.ApprovedAt.ShouldNotBeNull();
        approved.Version.ShouldNotBe(submitted.Version);
        // The list was evicted too.
        var listAfter = await client.SendAsync(Req(HttpMethod.Get, $"{Requests}?status=Approved", tenant), Ct);
        (await listAfter.Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct))!.Items.ShouldHaveSingleItem().Id.ShouldBe(submitted.Id);
        var submittedList = await client.SendAsync(Req(HttpMethod.Get, $"{Requests}?status=Submitted", tenant), Ct);
        (await submittedList.Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct))!.Total.ShouldBe(0);
    }

    [Fact]
    public async Task RequestRejected_message_sets_status_and_reason()
    {
        var tenant = NewTenant("rejected");
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));

        await PublishFromApprovalsAsync(_factory, Rejected(submitted, tenant, "Budget exceeded on CC-100", by: null), tenant, "tom");

        var rejected = await WaitForAsync(_client, tenant, submitted.Id, r => r.Status == RequestStatus.Rejected);
        rejected.RejectionReason.ShouldBe("Budget exceeded on CC-100");
        rejected.RejectedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Duplicate_or_late_outcome_is_a_no_op()
    {
        var tenant = NewTenant("dupout");
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));
        await PublishFromApprovalsAsync(_factory, Approved(submitted, tenant), tenant, DemoUsers.Maria);
        var approved = await WaitForAsync(_client, tenant, submitted.Id, r => r.Status == RequestStatus.Approved);

        // A redelivered approval and a rejection that lost the race: neither changes the approved request.
        await PublishFromApprovalsAsync(_factory, Approved(submitted, tenant), tenant, DemoUsers.Maria);
        await PublishFromApprovalsAsync(_factory, Rejected(submitted, tenant, "Too late"), tenant, DemoUsers.Maria);
        await WaitForHandledAsync(submitted.Id, count: 3);

        var after = await GetAsync(_client, tenant, submitted.Id);
        after.Status.ShouldBe(RequestStatus.Approved);
        after.Version.ShouldBe(approved.Version);
        after.RejectionReason.ShouldBeNull();
    }

    [Fact]
    public async Task Outcome_for_another_tenants_request_changes_nothing()
    {
        var tenant = NewTenant("own");
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));
        var other = NewTenant("other");

        // The event names another tenant (and the headers say so too): the request of `tenant` is not that tenant's.
        await PublishFromApprovalsAsync(_factory, Approved(submitted, other), other, DemoUsers.Maria);
        await WaitForHandledAsync(submitted.Id, count: 1);

        (await GetAsync(_client, tenant, submitted.Id)).Status.ShouldBe(RequestStatus.Submitted);
    }

    /// <summary>
    /// Waits until the outcome handler logged <paramref name="count"/> outcomes (applied or ignored) for the request: every
    /// factory logs to the shared sink, whichever instance took the message.
    /// </summary>
    private static async Task WaitForHandledAsync(Guid requestId, int count)
    {
        var deadline = DateTime.UtcNow + MessageTimeout;
        while (true)
        {
            var handled = CapturingSink.Shared.Events.Count(e =>
                e.MessageTemplate.Text.StartsWith("Outcome", StringComparison.Ordinal)
                && e.Properties.TryGetValue("RequestId", out var id) && id.ToString().Contains(requestId.ToString(), StringComparison.Ordinal));
            if (handled >= count)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Only {handled} of {count} outcome messages for {requestId} were handled.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    [Fact]
    public async Task Message_driven_status_change_is_audited_as_service_for_user()
    {
        var tenant = NewTenant("auditmsg");
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));

        // The approval saga passes the approver who completed the chain on as the user of RequestApproved.
        await PublishFromApprovalsAsync(_factory, Approved(submitted, tenant, DemoUsers.Dept), tenant, DemoUsers.Dept);
        await WaitForAsync(_client, tenant, submitted.Id, r => r.Status == RequestStatus.Approved);

        await using var db = new RequestsDbContext(
            new DbContextOptionsBuilder<RequestsDbContext>().UseNpgsql(infra.RequestsDbConnectionString).Options);
        var entries = await db.Set<AuditEntry>()
            .Where(e => e.Table == "purchase_requests" && e.PrimaryKey == submitted.Id.ToString() && e.Operation == AuditOperation.Update)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(Ct);
        var approval = entries.Last();
        approval.Changes.ShouldContain("Approved");
        approval.UserId.ShouldBe(DemoUsers.Dept);
        approval.ActorId.ShouldBe("service:requests-api");
        approval.TenantId.ShouldBe(tenant);
    }

    // ---- Cancel -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_publishes_RequestCancelled_once_for_ten_parallel_posts()
    {
        // Two instances; ten different idempotency keys with the same If-Match: exactly one cancel wins.
        await using var second = new ApiFactory(infra);
        using var clientB = second.CreateClient();
        var tenant = NewTenant("cancel10");
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));
        await using var recorder = await RabbitRecorder.StartAsync(infra.RabbitConnectionString, [Exchanges.Requests], []);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            var client = i % 2 == 0 ? _client : clientB;
            using var response = await client.SendAsync(
                Req(HttpMethod.Post, $"{Requests}/{submitted.Id}/cancel", tenant, new { reason = "Not needed any more" }, ifMatch: ETagOf(submitted)),
                Ct);
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync(Ct), ETag: response.Headers.ETag?.Tag);
        }));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1, string.Join(" | ", responses.Select(r => $"{r.StatusCode} {r.Body}")));
        responses.Where(r => r.StatusCode != HttpStatusCode.OK).ShouldAllBe(r => r.StatusCode == HttpStatusCode.PreconditionFailed);
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var cancelled = JsonSerializer.Deserialize<RequestDto>(winner.Body, Json)!;
        cancelled.Status.ShouldBe(RequestStatus.Cancelled);
        cancelled.CancellationReason.ShouldBe("Not needed any more");
        winner.ETag.ShouldBe(ETagOf(cancelled));

        var messages = await recorder.WaitForAsync(submitted.Id, "RequestCancelled", MessageTimeout);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        recorder.Of(submitted.Id).Count(m => m.Kind == "RequestCancelled").ShouldBe(1);
        var message = messages.First(m => m.Kind == "RequestCancelled");
        message.Text("tenantId").ShouldBe(tenant);
        message.Text("number").ShouldBe(submitted.Number);
        message.Text("cancelledBy").ShouldBe("claude-admin");
        message.Text("reason").ShouldBe("Not needed any more");
        (await GetAsync(_client, tenant, submitted.Id)).Status.ShouldBe(RequestStatus.Cancelled);
    }

    [Fact]
    public async Task Same_cancel_retried_with_its_key_gets_the_same_answer()
    {
        var tenant = NewTenant("cancelkey");
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));
        var key = Guid.NewGuid().ToString();

        var first = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{submitted.Id}/cancel", tenant, ifMatch: ETagOf(submitted), idempotencyKey: key), Ct);
        var retry = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{submitted.Id}/cancel", tenant, ifMatch: ETagOf(submitted), idempotencyKey: key), Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retry.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Cancel_of_a_draft_publishes_nothing()
    {
        var tenant = NewTenant("canceldraft");
        var created = await CreateAsync(tenant);
        await using var recorder = await RabbitRecorder.StartAsync(infra.RabbitConnectionString, [Exchanges.Requests], []);

        var response = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{created.Id}/cancel", tenant, ifMatch: ETagOf(created)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!.Status.ShouldBe(RequestStatus.Cancelled);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        recorder.Of(created.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task Other_users_request_cannot_be_cancelled_403()
    {
        var tenant = NewTenant("cancel403");
        var alice = TestAuth.CreateToken("alice", tenant, "requester");
        var bob = TestAuth.CreateToken("bob", tenant, "requester");
        var created = await CreateAsync(tenant, alice);

        var byBob = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{created.Id}/cancel", tenant, bearer: bob, ifMatch: ETagOf(created)), Ct);
        // The admin key acts for the user it names: on behalf of bob it is bob, and bob does not own the request.
        var keyForBob = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{created.Id}/cancel", tenant, ifMatch: ETagOf(created), onBehalfOf: "bob"), Ct);
        // Forbidden is decided before the version: a stale If-Match from bob is still 403, not 412.
        var staleByBob = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{created.Id}/cancel", tenant, bearer: bob, ifMatch: "\"1\""), Ct);

        byBob.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        keyForBob.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        staleByBob.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(_client, tenant, created.Id)).Status.ShouldBe(RequestStatus.Draft);

        // Alice herself, and the key on behalf of alice, may.
        var byAlice = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{created.Id}/cancel", tenant, ifMatch: ETagOf(created), onBehalfOf: "alice"), Ct);
        byAlice.StatusCode.ShouldBe(HttpStatusCode.OK, await byAlice.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Cancel_needs_if_match_and_a_rejected_request_cannot_be_cancelled()
    {
        var tenant = NewTenant("cancelrules");
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));
        await PublishFromApprovalsAsync(_factory, Rejected(submitted, tenant, "No"), tenant, DemoUsers.Maria);
        var rejected = await WaitForAsync(_client, tenant, submitted.Id, r => r.Status == RequestStatus.Rejected);

        var noIfMatch = await _client.SendAsync(Req(HttpMethod.Post, $"{Requests}/{submitted.Id}/cancel", tenant), Ct);
        var ofRejected = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{submitted.Id}/cancel", tenant, ifMatch: ETagOf(rejected)), Ct);

        noIfMatch.StatusCode.ShouldBe((HttpStatusCode)428);
        ofRejected.StatusCode.ShouldBe((HttpStatusCode)422);
    }
}
