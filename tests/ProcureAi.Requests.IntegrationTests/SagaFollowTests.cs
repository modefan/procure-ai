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
using ProcureAi.BuildingBlocks.Domain;
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
            supplierId = "contoso-hardware",
            lines = new[] { new { sku = "LAP-1", quantity = 3 } },
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

        // A redelivered approval and a confirmation of a cancel nobody asked for: neither changes the approved request.
        // (A rejection is no longer late: after approval it is the supplier's, and rejects the request.)
        await PublishFromApprovalsAsync(_factory, Approved(submitted, tenant), tenant, DemoUsers.Maria);
        await PublishFromApprovalsAsync(
            _factory, new RequestCancellationConfirmed(submitted.Id, tenant, submitted.Number, DateTimeOffset.UtcNow), tenant, DemoUsers.Maria);
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

    // ---- Ordering, cancellation outcome and completion (plan 03) ----------------------------------------------------------

    private static RequestOrdered Ordered(RequestDto request, string tenant, Guid orderId, string number = "PO-0001") =>
        new(request.Id, tenant, request.Number, orderId, number, DateTimeOffset.UtcNow);

    private static RequestCompleted Completed(RequestDto request, string tenant, decimal spent = 4200m) =>
        new(request.Id, tenant, request.Number, spent, DateTimeOffset.UtcNow);

    private async Task<RequestDto> ApprovedAsync(string tenant)
    {
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));
        await PublishFromApprovalsAsync(_factory, Approved(submitted, tenant), tenant, DemoUsers.Maria);
        return await WaitForAsync(_client, tenant, submitted.Id, r => r.Status == RequestStatus.Approved);
    }

    private async Task<RequestDto> OrderedAsync(string tenant, Guid orderId)
    {
        var approved = await ApprovedAsync(tenant);
        await PublishFromApprovalsAsync(_factory, Ordered(approved, tenant, orderId), tenant, DemoUsers.Maria);
        return await WaitForAsync(_client, tenant, approved.Id, r => r.Status == RequestStatus.Ordered);
    }

    private async Task<RequestDto> CancellingAsync(string tenant, RequestDto request)
    {
        var response = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{request.Id}/cancel", tenant, new { reason = "Changed our mind" }, ifMatch: ETagOf(request)), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var cancelling = (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
        cancelling.Status.ShouldBe(RequestStatus.Cancelling);
        return cancelling;
    }

    /// <summary>Handled messages (any outcome, applied or ignored) of the request, as the shared log sink saw them.</summary>
    private static int HandledCount(Guid requestId) =>
        CapturingSink.Shared.Events.Count(e =>
            e.MessageTemplate.Text.StartsWith("Outcome", StringComparison.Ordinal)
            && e.Properties.TryGetValue("RequestId", out var id) && id.ToString().Contains(requestId.ToString(), StringComparison.Ordinal));

    /// <summary>Rows of Wolverine dead-letter table plus messages in the broker dead-letter queue.</summary>
    private async Task<long> DeadLettersAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(infra.RequestsDbConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM wolverine.wolverine_dead_letters", connection);
        var rows = (long)(await command.ExecuteScalarAsync(Ct))!;
        await using var rabbit = await new RabbitMQ.Client.ConnectionFactory { Uri = new Uri(infra.RabbitConnectionString) }.CreateConnectionAsync(Ct);
        await using var channel = await rabbit.CreateChannelAsync(cancellationToken: Ct);
        try
        {
            return rows + (await channel.QueueDeclarePassiveAsync("wolverine-dead-letter-queue", Ct)).MessageCount;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return rows;
        }
    }

    private async Task WaitForHandledMoreAsync(Guid requestId, int target)
    {
        var deadline = DateTime.UtcNow + MessageTimeout;
        while (HandledCount(requestId) < target)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, $"Only {HandledCount(requestId)} of {target} messages for {requestId} were handled.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    [Fact]
    public async Task RequestOrdered_sets_Ordered_and_fresh_get_within_1s()
    {
        // A 60 s GET cache: only an eviction, not expiry, can make the next read fresh.
        await using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Caching:DefaultGetSeconds", "60"));
        using var client = factory.CreateClient();
        var tenant = NewTenant("ordered");
        var approved = await ApprovedAsync(tenant);
        (await GetAsync(client, tenant, approved.Id)).Status.ShouldBe(RequestStatus.Approved);
        var orderId = Guid.NewGuid();

        await PublishFromApprovalsAsync(factory, Ordered(approved, tenant, orderId), tenant, DemoUsers.Maria);

        var ordered = await WaitForAsync(client, tenant, approved.Id, r => r.Status == RequestStatus.Ordered);
        ordered.OrderId.ShouldBe(orderId);
        ordered.OrderNumber.ShouldBe("PO-0001");
        var list = await client.SendAsync(Req(HttpMethod.Get, $"{Requests}?status=Ordered", tenant), Ct);
        (await list.Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct))!.Items.ShouldHaveSingleItem().Id.ShouldBe(approved.Id);
    }

    [Fact]
    public async Task Cancel_of_an_ordered_request_is_cancelling_and_publishes_once()
    {
        var tenant = NewTenant("cancelord");
        var ordered = await OrderedAsync(tenant, Guid.NewGuid());
        await using var recorder = await RabbitRecorder.StartAsync(infra.RabbitConnectionString, [Exchanges.Requests], []);

        var cancelling = await CancellingAsync(tenant, ordered);

        cancelling.CancellationReason.ShouldBe("Changed our mind");
        cancelling.CancelledAt.ShouldNotBeNull();
        await recorder.WaitForAsync(ordered.Id, "RequestCancelled", MessageTimeout);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        recorder.Of(ordered.Id).Count(m => m.Kind == "RequestCancelled").ShouldBe(1);
        (await GetAsync(_client, tenant, ordered.Id)).Status.ShouldBe(RequestStatus.Cancelling);
    }

    [Fact]
    public async Task Cancellation_confirmed_sets_Cancelled()
    {
        var tenant = NewTenant("confirm");
        var ordered = await OrderedAsync(tenant, Guid.NewGuid());
        await CancellingAsync(tenant, ordered);

        await PublishFromApprovalsAsync(
            _factory, new RequestCancellationConfirmed(ordered.Id, tenant, ordered.Number, DateTimeOffset.UtcNow), tenant, "tom");

        var cancelled = await WaitForAsync(_client, tenant, ordered.Id, r => r.Status == RequestStatus.Cancelled);
        cancelled.CancellationReason.ShouldBe("Changed our mind");
        cancelled.CancelledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancellation_refused_after_goods_restores_Ordered_with_reason()
    {
        var tenant = NewTenant("refuse");
        var ordered = await OrderedAsync(tenant, Guid.NewGuid());
        await CancellingAsync(tenant, ordered);

        await PublishFromApprovalsAsync(
            _factory, new RequestCancellationRefused(ordered.Id, tenant, ordered.Number, "Goods were received", DateTimeOffset.UtcNow), tenant, "tom");

        var restored = await WaitForAsync(_client, tenant, ordered.Id, r => r.Status == RequestStatus.Ordered);
        restored.CancellationRefusedReason.ShouldBe("Goods were received");
        restored.CancellationReason.ShouldBeNull();
        restored.CancelledAt.ShouldBeNull();
        restored.OrderNumber.ShouldBe("PO-0001");
    }

    [Fact]
    public async Task RequestCompleted_sets_Completed_with_spent()
    {
        var tenant = NewTenant("completed");
        var ordered = await OrderedAsync(tenant, Guid.NewGuid());

        await PublishFromApprovalsAsync(_factory, Completed(ordered, tenant, 4200m), tenant, "tom");

        var completed = await WaitForAsync(_client, tenant, ordered.Id, r => r.Status == RequestStatus.Completed);
        completed.SpentInclVat.ShouldBe(4200m);
        completed.SpentInclVatDisplay.ShouldBe(Money.Zar(4200m).ToDisplayString());
        completed.SpentInclVatDisplay!.ShouldStartWith("R");
        completed.OrderNumber.ShouldBe("PO-0001");
    }

    [Fact]
    public async Task Cancel_completed_request_422()
    {
        var tenant = NewTenant("cancelcomp");
        var ordered = await OrderedAsync(tenant, Guid.NewGuid());
        await PublishFromApprovalsAsync(_factory, Completed(ordered, tenant), tenant, "tom");
        var completed = await WaitForAsync(_client, tenant, ordered.Id, r => r.Status == RequestStatus.Completed);

        var response = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{ordered.Id}/cancel", tenant, ifMatch: ETagOf(completed)), Ct);

        response.StatusCode.ShouldBe((HttpStatusCode)422, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Completed_while_cancelling_clears_the_cancel_and_the_late_refusal_and_confirmation_are_no_ops()
    {
        var tenant = NewTenant("compcancel");
        var ordered = await OrderedAsync(tenant, Guid.NewGuid());
        await CancellingAsync(tenant, ordered);
        var dead = await DeadLettersAsync();

        await PublishFromApprovalsAsync(_factory, Completed(ordered, tenant, 1000m), tenant, "tom");
        var completed = await WaitForAsync(_client, tenant, ordered.Id, r => r.Status == RequestStatus.Completed);
        completed.CancellationReason.ShouldBeNull();
        completed.CancelledAt.ShouldBeNull();

        // The refusal ("The order was settled") may be handled after the completion; so may a confirmation.
        var handled = HandledCount(ordered.Id);
        await PublishFromApprovalsAsync(
            _factory, new RequestCancellationRefused(ordered.Id, tenant, ordered.Number, "The order was settled", DateTimeOffset.UtcNow), tenant, "tom");
        await PublishFromApprovalsAsync(
            _factory, new RequestCancellationConfirmed(ordered.Id, tenant, ordered.Number, DateTimeOffset.UtcNow), tenant, "tom");
        await WaitForHandledMoreAsync(ordered.Id, handled + 2);

        var after = await GetAsync(_client, tenant, ordered.Id);
        after.Status.ShouldBe(RequestStatus.Completed);
        after.Version.ShouldBe(completed.Version);
        after.CancellationRefusedReason.ShouldBeNull();
        (await DeadLettersAsync()).ShouldBe(dead);
    }

    [Fact]
    public async Task Order_settled_before_it_was_announced_completes_whatever_the_order_of_the_messages()
    {
        var tenant = NewTenant("overtaken");
        var approved = await ApprovedAsync(tenant);
        var orderId = Guid.NewGuid();
        var dead = await DeadLettersAsync();

        // RequestCompleted first (while Approved), then RequestOrdered with no number (the settlement carried none).
        await PublishFromApprovalsAsync(_factory, Completed(approved, tenant, 3000m), tenant, "tom");
        var completed = await WaitForAsync(_client, tenant, approved.Id, r => r.Status == RequestStatus.Completed);
        completed.SpentInclVat.ShouldBe(3000m);
        completed.OrderNumber.ShouldBeNull();
        await PublishFromApprovalsAsync(_factory, Ordered(approved, tenant, orderId, number: ""), tenant, "tom");
        var withOrder = await WaitForAsync(_client, tenant, approved.Id, r => r.OrderId == orderId);

        withOrder.Status.ShouldBe(RequestStatus.Completed);
        withOrder.OrderNumber.ShouldBeNull();
        // A later message that knows the number fills it in; an empty one never erases it.
        await PublishFromApprovalsAsync(_factory, Ordered(approved, tenant, orderId, number: "PO-0009"), tenant, "tom");
        await WaitForAsync(_client, tenant, approved.Id, r => r.OrderNumber == "PO-0009");
        var handled = HandledCount(approved.Id);
        await PublishFromApprovalsAsync(_factory, Ordered(approved, tenant, orderId, number: ""), tenant, "tom");
        await WaitForHandledMoreAsync(approved.Id, handled + 1);
        (await GetAsync(_client, tenant, approved.Id)).OrderNumber.ShouldBe("PO-0009");
        (await DeadLettersAsync()).ShouldBe(dead);
    }

    [Fact]
    public async Task Supplier_rejection_rejects_an_ordered_request()
    {
        var tenant = NewTenant("suprej");
        var ordered = await OrderedAsync(tenant, Guid.NewGuid());

        await PublishFromApprovalsAsync(_factory, Rejected(ordered, tenant, "The supplier cannot deliver", by: null), tenant, "tom");

        var rejected = await WaitForAsync(_client, tenant, ordered.Id, r => r.Status == RequestStatus.Rejected);
        rejected.RejectionReason.ShouldBe("The supplier cannot deliver");
    }

    [Fact]
    public async Task Cancel_of_an_approved_request_is_cancelling_until_the_saga_confirms()
    {
        var tenant = NewTenant("cancelappr");
        var approved = await ApprovedAsync(tenant);

        var cancelling = await CancellingAsync(tenant, approved);

        cancelling.Status.ShouldBe(RequestStatus.Cancelling);
        await PublishFromApprovalsAsync(
            _factory, new RequestCancellationConfirmed(approved.Id, tenant, approved.Number, DateTimeOffset.UtcNow), tenant, "tom");
        await WaitForAsync(_client, tenant, approved.Id, r => r.Status == RequestStatus.Cancelled);
    }

    [Fact]
    public async Task Ordered_before_Approved_still_orders_and_the_late_approval_changes_nothing()
    {
        var tenant = NewTenant("ordfirst");
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));
        var dead = await DeadLettersAsync();

        await PublishFromApprovalsAsync(_factory, Ordered(submitted, tenant, Guid.NewGuid()), tenant, "tom");
        var ordered = await WaitForAsync(_client, tenant, submitted.Id, r => r.Status == RequestStatus.Ordered);
        var handled = HandledCount(submitted.Id);
        await PublishFromApprovalsAsync(_factory, Approved(submitted, tenant), tenant, DemoUsers.Maria);
        await WaitForHandledMoreAsync(submitted.Id, handled + 1);

        var after = await GetAsync(_client, tenant, submitted.Id);
        after.Status.ShouldBe(RequestStatus.Ordered);
        after.Version.ShouldBe(ordered.Version);
        (await DeadLettersAsync()).ShouldBe(dead);
    }

    [Fact]
    public async Task Completed_before_Approved_still_completes_and_the_late_approval_changes_nothing()
    {
        var tenant = NewTenant("compfirst");
        var submitted = await SubmitAsync(tenant, await CreateAsync(tenant));
        var dead = await DeadLettersAsync();

        await PublishFromApprovalsAsync(_factory, Completed(submitted, tenant, 500m), tenant, "tom");
        var completed = await WaitForAsync(_client, tenant, submitted.Id, r => r.Status == RequestStatus.Completed);
        var handled = HandledCount(submitted.Id);
        await PublishFromApprovalsAsync(_factory, Approved(submitted, tenant), tenant, DemoUsers.Maria);
        await WaitForHandledMoreAsync(submitted.Id, handled + 1);

        var after = await GetAsync(_client, tenant, submitted.Id);
        after.Status.ShouldBe(RequestStatus.Completed);
        after.SpentInclVat.ShouldBe(500m);
        after.Version.ShouldBe(completed.Version);
        (await DeadLettersAsync()).ShouldBe(dead);
    }

    [Fact]
    public async Task Supplier_rejection_while_cancelling_rejects_the_request()
    {
        var tenant = NewTenant("rejcancel");
        var ordered = await OrderedAsync(tenant, Guid.NewGuid());
        await CancellingAsync(tenant, ordered);

        await PublishFromApprovalsAsync(_factory, Rejected(ordered, tenant, "The supplier cannot deliver", by: null), tenant, "tom");

        var rejected = await WaitForAsync(_client, tenant, ordered.Id, r => r.Status == RequestStatus.Rejected);
        rejected.RejectionReason.ShouldBe("The supplier cannot deliver");
        rejected.CancellationReason.ShouldBeNull();
        rejected.CancelledAt.ShouldBeNull();
    }
}
