using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ProcureAi.Approvals.Application.Dtos;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using Shouldly;
using static ProcureAi.Approvals.IntegrationTests.ApprovalsApp;

namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>
/// The HTTP API of Approvals: the inbox, approve and reject with If-Match and Idempotency-Key, the chain of a request and the
/// approval preview, on the real API with real Postgres, RabbitMQ and Redis. Chains are set up in-process (the saga's own
/// messages), the API is called over HTTP as a user, the admin API key or another service.
/// </summary>
[Collection(ApprovalsCollection.Name)]
public sealed class ApiTests(ApprovalsApp app)
{
    private const string Base = "/api/v1/approvals";

    /// <summary>How long a test waits before asserting that something did NOT happen.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    /// <summary>How many requests a race test runs (each its own approval racing its own timeout).</summary>
    private const int RaceRounds = 20;

    /// <summary>Round n starts the timeout n times this after the HTTP call, so the rounds cover the call's duration.</summary>
    private static readonly TimeSpan RaceStagger = TimeSpan.FromMilliseconds(10);

    /// <summary>How long a test waits for an eviction made by a saga handler (after its commit, through a local message).</summary>
    private static readonly TimeSpan EvictionTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApprovalsInstance Main => app.Main;

    private RabbitRecorder Recorder => app.Recorder;

    // ---- the inbox ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inbox_shows_maria_her_pending_step_only()
    {
        var tenant = NewTenant();
        var otherTenant = NewTenant();
        app.UseAcme(tenant);
        app.UseAcme(otherTenant);
        // A: R 30 000, step 1 maria (open), step 2 the department head (not open yet).
        var (a, aSteps) = await OpenAsync(Main, tenant, DemoUsers.Tom, 30_000m);
        // B: maria's step, already approved.
        var (b, bSteps) = await OpenAsync(Main, tenant, DemoUsers.Tom, 5_000m);
        await Main.InvokeAsync(new ApproveStep(b, bSteps[0].Id, bSteps[0].Version), tenant, DemoUsers.Maria);
        // C: maria's open step in another tenant.
        await OpenAsync(Main, otherTenant, DemoUsers.Tom, 5_000m);
        using var client = Main.CreateClient();

        var maria = await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", User(tenant, DemoUsers.Maria, "manager")), Ct));
        var dept = await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", User(tenant, DemoUsers.Dept, "department-head")), Ct));
        var requester = await client.SendAsync(Get("inbox", User(tenant, DemoUsers.Tom, "requester")), Ct);

        maria.Total.ShouldBe(1);
        var item = maria.Items.ShouldHaveSingleItem();
        item.Id.ShouldBe(aSteps[0].Id);
        item.RequestId.ShouldBe(a);
        item.RequesterId.ShouldBe(DemoUsers.Tom);
        item.TotalInclVat.ShouldBe(30_000m);
        item.TotalInclVatDisplay.ShouldBe(Money.Zar(30_000m).ToDisplayString());
        item.StepNo.ShouldBe(1);
        item.Level.ShouldBe("Manager");
        item.Status.ShouldBe("Pending");
        item.DueAt.ShouldNotBeNull();
        item.CanAct.ShouldBeTrue();
        item.CannotActReason.ShouldBeNull();
        item.Version.ShouldBe(aSteps[0].Version);
        dept.Total.ShouldBe(0, "the department-head step is not open until maria approved");
        requester.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the inbox needs requests.approve");
    }

    [Fact]
    public async Task Inbox_after_approve_is_fresh_within_1s()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        // Reads cached for 30 s: only the eviction by the write can make them fresh.
        await using var instance = await app.StartFastInstanceAsync(ManualTimeoutsLongCache);
        var (_, steps) = await OpenAsync(instance, tenant, DemoUsers.Tom, 30_000m);
        using var client = instance.CreateClient();
        var maria = User(tenant, DemoUsers.Maria, "manager");
        var dept = User(tenant, DemoUsers.Dept, "department-head");
        (await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", maria), Ct))).Total.ShouldBe(1);
        (await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", dept), Ct))).Total.ShouldBe(0);

        var approved = await client.SendAsync(Post($"{steps[0].Id}/approve", maria, Tag(steps[0].Version)), Ct);
        var mariaAfter = await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", maria), Ct));
        var deptAfter = await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", dept), Ct));

        var step = await ReadAsync<ApprovalStepDto>(approved);
        step.Status.ShouldBe("Approved");
        step.CanAct.ShouldBeFalse();
        approved.Headers.ETag!.Tag.ShouldBe($"\"{step.Version}\"");
        mariaAfter.Total.ShouldBe(0, "maria's cached inbox was evicted by her approval");
        deptAfter.Items.ShouldHaveSingleItem().Id.ShouldBe(steps[1].Id, "every approver's inbox of the tenant was evicted");
    }

    [Fact]
    public async Task Saga_changes_evict_the_inbox_and_the_chain()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        await using var instance = await app.StartFastInstanceAsync(ManualTimeoutsLongCache);
        var (id, steps) = await OpenAsync(instance, tenant, DemoUsers.Tom, 5_000m);
        using var client = instance.CreateClient();
        var maria = User(tenant, DemoUsers.Maria, "manager");
        var dept = User(tenant, DemoUsers.Dept, "department-head");
        var tom = User(tenant, DemoUsers.Tom, "requester");
        (await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", maria), Ct))).Total.ShouldBe(1);
        (await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", dept), Ct))).Total.ShouldBe(0);
        (await ReadAsync<ApprovalChainDto>(await client.SendAsync(Get($"requests/{id}", tom), Ct))).Steps.Count.ShouldBe(1);

        // The escalation is message-driven: only the saga's own eviction can refresh the cached reads.
        await instance.InvokeAsync(new StepTimeout(id, steps[0].Id, StepTimeoutKind.Escalate, 1), tenant, DemoUsers.Tom);

        await EventuallyAsync(async () =>
            (await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", maria), Ct))).Total == 0);
        await EventuallyAsync(async () =>
            (await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", dept), Ct))).Total == 1);
        await EventuallyAsync(async () =>
            (await ReadAsync<ApprovalChainDto>(await client.SendAsync(Get($"requests/{id}", tom), Ct))).Steps.Count == 2);

        await instance.InvokeAsync(Cancelled(Submitted(tenant, DemoUsers.Tom, 5_000m) with { RequestId = id }), tenant, DemoUsers.Tom);
        await EventuallyAsync(async () =>
            (await ReadAsync<ApprovalChainDto>(await client.SendAsync(Get($"requests/{id}", tom), Ct))).State == nameof(ApprovalState.Cancelled));
        await EventuallyAsync(async () =>
            (await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", dept), Ct))).Total == 0);
    }

    // ---- approve and reject -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_parallel_approvals_of_one_step_produce_one_transition()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var (id, steps) = await OpenAsync(Main, tenant, DemoUsers.Tom, 5_000m);
        await using var second = app.StartSecondMainInstance();
        using var first = Main.CreateClient();
        using var other = second.CreateClient();
        var maria = User(tenant, DemoUsers.Maria, "manager");

        // A double click or two tabs, spread over two instances: same If-Match, a new Idempotency-Key each time.
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            (i % 2 == 0 ? first : other).SendAsync(Post($"{steps[0].Id}/approve", maria, Tag(steps[0].Version)), Ct)));

        var statuses = responses.Select(r => (int)r.StatusCode).ToList();
        statuses.Count(s => s == 200).ShouldBe(1, string.Join(",", statuses));
        statuses.Where(s => s != 200).ShouldAllBe(s => s == 409 || s == 412, string.Join(",", statuses));
        await Recorder.WaitForAsync(id, nameof(ApprovalStepApproved), EventTimeout);
        await Task.Delay(Settle, Ct);
        Recorder.Of(id).Count(m => m.Kind == nameof(ApprovalStepApproved)).ShouldBe(1);
        Recorder.Of(id).Count(m => m.Kind == nameof(RequestApproved)).ShouldBe(1);
        var step = (await Main.StepsAsync(id)).Single();
        step.Status.ShouldBe(ApprovalStepStatus.Approved);
        step.ActedBy.ShouldBe(DemoUsers.Maria);
    }

    [Fact]
    public async Task Same_idempotency_key_ten_times_gives_identical_responses()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var (id, steps) = await OpenAsync(Main, tenant, DemoUsers.Tom, 5_000m);
        using var client = Main.CreateClient();
        var maria = User(tenant, DemoUsers.Maria, "manager");
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            client.SendAsync(Post($"{steps[0].Id}/approve", maria, Tag(steps[0].Version), key: key), Ct)));

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync(Ct)));
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK, string.Join(",", responses.Select(r => (int)r.StatusCode)));
        bodies.Distinct().ShouldHaveSingleItem();
        responses.Select(r => r.Headers.ETag!.Tag).Distinct().ShouldHaveSingleItem();
        await Recorder.WaitForAsync(id, nameof(ApprovalStepApproved), EventTimeout);
        await Task.Delay(Settle, Ct);
        Recorder.Of(id).Count(m => m.Kind == nameof(ApprovalStepApproved)).ShouldBe(1);
    }

    [Fact]
    public async Task Admin_key_on_behalf_of_requester_cannot_approve_403()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var (id, steps) = await OpenAsync(Main, tenant, DemoUsers.Tom, 5_000m);
        using var client = Main.CreateClient();
        var url = $"{steps[0].Id}/approve";

        var asRequester = await client.SendAsync(Post(url, AdminKey(tenant, onBehalfOf: DemoUsers.Tom), Tag(steps[0].Version)), Ct);
        var asNobody = await client.SendAsync(Post(url, AdminKey(tenant), Tag(steps[0].Version)), Ct);
        (await Main.StepsAsync(id)).Single().Status.ShouldBe(ApprovalStepStatus.Pending);
        var asApprover = await client.SendAsync(Post(url, AdminKey(tenant, onBehalfOf: DemoUsers.Maria), Tag(steps[0].Version)), Ct);

        asRequester.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "separation of duties applies to the user the key acts for");
        (await asRequester.Content.ReadAsStringAsync(Ct)).ShouldContain("You raised this request");
        asNobody.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the key itself is no approver of the step");
        (await ReadAsync<ApprovalStepDto>(asApprover)).Status.ShouldBe("Approved");
        (await Main.StepsAsync(id)).Single().ActedBy.ShouldBe(DemoUsers.Maria);
    }

    [Fact]
    public async Task Approve_without_if_match_428()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var (id, steps) = await OpenAsync(Main, tenant, DemoUsers.Tom, 5_000m);
        using var client = Main.CreateClient();
        var maria = User(tenant, DemoUsers.Maria, "manager");

        var approve = await client.SendAsync(Post($"{steps[0].Id}/approve", maria, ifMatch: null), Ct);
        var reject = await client.SendAsync(Post($"{steps[0].Id}/reject", maria, ifMatch: null, body: new { reason = "Too much" }), Ct);
        var notAVersion = await client.SendAsync(Post($"{steps[0].Id}/approve", maria, "\"abc\""), Ct);
        var stale = await client.SendAsync(Post($"{steps[0].Id}/approve", maria, Tag(steps[0].Version + 1)), Ct);
        var noKey = await client.SendAsync(Post($"{steps[0].Id}/approve", maria, Tag(steps[0].Version), key: ""), Ct);

        ((int)approve.StatusCode).ShouldBe(428);
        ((int)reject.StatusCode).ShouldBe(428);
        notAVersion.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "approve is [Idempotent]");
        (await Main.StepsAsync(id)).Single().Status.ShouldBe(ApprovalStepStatus.Pending);
    }

    [Fact]
    public async Task Reject_needs_a_reason_and_rejects_the_request()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var (id, steps) = await OpenAsync(Main, tenant, DemoUsers.Tom, 30_000m);
        using var client = Main.CreateClient();
        var maria = User(tenant, DemoUsers.Maria, "manager");
        var url = $"{steps[0].Id}/reject";

        var missing = await client.SendAsync(Post(url, maria, Tag(steps[0].Version), body: new { }), Ct);
        var tooShort = await client.SendAsync(Post(url, maria, Tag(steps[0].Version), body: new { reason = "no" }), Ct);
        var tooLong = await client.SendAsync(Post(url, maria, Tag(steps[0].Version), body: new { reason = new string('x', 501) }), Ct);
        var rejected = await client.SendAsync(Post(url, maria, Tag(steps[0].Version), body: new { reason = "Too expensive" }), Ct);

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooShort.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync<ApprovalStepDto>(rejected)).Status.ShouldBe("Rejected");
        var event_ = (await Recorder.WaitForAsync(id, nameof(RequestRejected), EventTimeout)).Single(m => m.Kind == nameof(RequestRejected));
        event_.Text("reason").ShouldBe("Too expensive");
        event_.Text("rejectedBy").ShouldBe(DemoUsers.Maria);
        // The saga ended (it is deleted); the chain still answers, from the steps.
        var chain = await ReadAsync<ApprovalChainDto>(await client.SendAsync(Get($"requests/{id}", User(tenant, DemoUsers.Tom, "requester")), Ct));
        chain.State.ShouldBe(nameof(ApprovalState.Rejected));
        chain.Steps.Select(s => s.Status).ShouldBe(["Rejected", "Cancelled"]);
    }

    [Fact]
    public async Task Other_tenant_step_404()
    {
        var tenant = NewTenant();
        var otherTenant = NewTenant();
        app.UseAcme(tenant);
        app.UseAcme(otherTenant);
        var (id, steps) = await OpenAsync(Main, tenant, DemoUsers.Tom, 5_000m);
        using var client = Main.CreateClient();
        var mariaElsewhere = User(otherTenant, DemoUsers.Maria, "manager");

        var approve = await client.SendAsync(Post($"{steps[0].Id}/approve", mariaElsewhere, Tag(steps[0].Version)), Ct);
        var reject = await client.SendAsync(Post($"{steps[0].Id}/reject", mariaElsewhere, Tag(steps[0].Version), body: new { reason = "Not mine" }), Ct);
        var chain = await client.SendAsync(Get($"requests/{id}", mariaElsewhere), Ct);
        var keyElsewhere = await client.SendAsync(Post($"{steps[0].Id}/approve", AdminKey(otherTenant, onBehalfOf: DemoUsers.Maria), Tag(steps[0].Version)), Ct);
        var unknown = await client.SendAsync(Post($"{Guid.NewGuid()}/approve", User(tenant, DemoUsers.Maria, "manager"), Tag(1)), Ct);

        approve.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        reject.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        chain.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        keyElsewhere.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the admin key acts in the tenant of X-Tenant-Id only");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Main.StepsAsync(id)).Single().Status.ShouldBe(ApprovalStepStatus.Pending);
    }

    [Fact]
    public async Task Chain_shows_the_steps_and_locks_them_for_the_requester()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        var (id, steps) = await OpenAsync(Main, tenant, DemoUsers.Tom, 30_000m);
        using var client = Main.CreateClient();

        var tom = await ReadAsync<ApprovalChainDto>(await client.SendAsync(Get($"requests/{id}", User(tenant, DemoUsers.Tom, "requester")), Ct));
        var maria = await ReadAsync<ApprovalChainDto>(await client.SendAsync(Get($"requests/{id}", User(tenant, DemoUsers.Maria, "manager")), Ct));
        var unknown = await client.SendAsync(Get($"requests/{Guid.NewGuid()}", User(tenant, DemoUsers.Tom, "requester")), Ct);

        tom.RequestId.ShouldBe(id);
        tom.State.ShouldBe(nameof(ApprovalState.AwaitingApproval));
        tom.Steps.Select(s => (s.Id, s.StepNo, s.Level)).ShouldBe([(steps[0].Id, 1, "Manager"), (steps[1].Id, 2, "DepartmentHead")]);
        tom.Steps.ShouldAllBe(s => !s.CanAct && s.CannotActReason == "You raised this request");
        maria.Steps[0].CanAct.ShouldBeTrue();
        maria.Steps[1].CanAct.ShouldBeFalse();
        maria.Steps[1].CannotActReason.ShouldNotBeNull();
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- races with the saga's own timeouts (carry-overs 1 and 2) ---------------------------------------------------------

    [Fact]
    public async Task Reminder_does_not_make_the_approvers_etag_stale()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        await using var instance = await app.StartManualInstanceAsync();
        var (id, steps) = await OpenAsync(instance, tenant, DemoUsers.Tom, 5_000m);
        using var client = instance.CreateClient();
        var maria = User(tenant, DemoUsers.Maria, "manager");
        var loaded = (await ReadAsync<PagedResult<ApprovalStepDto>>(await client.SendAsync(Get("inbox", maria), Ct))).Items.Single();

        await instance.InvokeAsync(new StepTimeout(id, steps[0].Id, StepTimeoutKind.Reminder, 1), tenant, DemoUsers.Tom);
        await Recorder.WaitForAsync(id, nameof(ApprovalReminderDue), EventTimeout);
        var approved = await client.SendAsync(Post($"{loaded.Id}/approve", maria, Tag(loaded.Version)), Ct);

        (await ReadAsync<ApprovalStepDto>(approved)).Status.ShouldBe("Approved", "a reminder is not a change of the step the approver saw");
    }

    [Fact]
    public async Task Reminder_racing_an_approval_never_fails_the_approval()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        await using var instance = await app.StartManualInstanceAsync();
        using var client = instance.CreateClient();
        var maria = User(tenant, DemoUsers.Maria, "manager");
        var chains = await OpenManyAsync(instance, tenant, RaceRounds);
        // Warm up (token, permissions from Access), so the approvals take about as long as they will in the race.
        (await client.SendAsync(Get("inbox", maria), Ct)).EnsureSuccessStatusCode();

        // Each approval runs at about the same moment as the step's reminder: the reminder starts 0, 10, 20, ... ms after the HTTP
        // call, across the time the call takes, so some rounds overlap. Both change the saga row; the one that commits second
        // loses the race and is retried.
        var results = await Task.WhenAll(chains.Select(async (chain, round) =>
        {
            var (id, steps) = chain;
            var approve = client.SendAsync(Post($"{steps[0].Id}/approve", maria, Tag(steps[0].Version)), Ct);
            await Task.Delay(round * RaceStagger, Ct);
            var reminder = instance.InvokeAsync(new StepTimeout(id, steps[0].Id, StepTimeoutKind.Reminder, 1), tenant, DemoUsers.Tom);
            await Task.WhenAll(reminder, approve);
            return (Id: id, Status: (int)(await approve).StatusCode, Body: await (await approve).Content.ReadAsStringAsync(Ct));
        }));

        foreach (var (id, status, body) in results)
        {
            status.ShouldBe(200, body);
            (await instance.StepsAsync(id)).Single().Status.ShouldBe(ApprovalStepStatus.Approved);
        }
    }

    [Fact]
    public async Task Escalation_racing_an_approval_gives_one_outcome_and_never_500()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        await using var instance = await app.StartManualInstanceAsync();
        using var client = instance.CreateClient();
        var maria = User(tenant, DemoUsers.Maria, "manager");
        var chains = await OpenManyAsync(instance, tenant, RaceRounds);
        // Warm up (token, permissions from Access), so the approvals take about as long as they will in the race.
        (await client.SendAsync(Get("inbox", maria), Ct)).EnsureSuccessStatusCode();

        var results = await Task.WhenAll(chains.Select(async (chain, round) =>
        {
            var (id, steps) = chain;
            var approve = client.SendAsync(Post($"{steps[0].Id}/approve", maria, Tag(steps[0].Version)), Ct);
            await Task.Delay(round * RaceStagger, Ct);
            var escalate = instance.InvokeAsync(new StepTimeout(id, steps[0].Id, StepTimeoutKind.Escalate, 1), tenant, DemoUsers.Tom);
            await Task.WhenAll(escalate, approve);
            return (Id: id, Step: steps[0].Id, Status: (int)(await approve).StatusCode, Body: await (await approve).Content.ReadAsStringAsync(Ct));
        }));

        foreach (var (id, stepId, status, body) in results)
        {
            new[] { 200, 409 }.ShouldContain(status, body);
            var first = (await instance.StepsAsync(id)).Single(s => s.Id == stepId);
            first.Status.ShouldBe(status == 200 ? ApprovalStepStatus.Approved : ApprovalStepStatus.Escalated, body);
        }
    }

    // ---- preview ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_for_R250000_lists_dept_then_thandi()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        using var client = Main.CreateClient();
        var query = $"costCenter={CostCenter}&amount=250000";

        var asTom = await ReadAsync<ApprovalPreviewDto>(await client.SendAsync(Get($"preview?{query}", User(tenant, DemoUsers.Tom, "requester")), Ct));
        var asService = await ReadAsync<ApprovalPreviewDto>(
            await client.SendAsync(Get($"internal/preview?{query}&requesterId={DemoUsers.Tom}", Service(tenant)), Ct));
        var internalAsUser = await client.SendAsync(Get($"internal/preview?{query}&requesterId={DemoUsers.Tom}", User(tenant, DemoUsers.Tom, "requester")), Ct);

        foreach (var preview in new[] { asTom, asService })
        {
            preview.Possible.ShouldBeTrue();
            preview.Reason.ShouldBeNull();
            // Maria's limit (R 200 000) is too low: the manager level moves up to the department head, then finance.
            preview.Steps.Select(s => $"{s.StepNo}:{s.Level}:{string.Join(",", s.ApproverNames)}").ShouldBe(["1:DepartmentHead:Dept", "2:Finance:Thandi"]);
        }

        internalAsUser.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Preview_for_thandi_R250000_not_possible_with_reason()
    {
        var tenant = NewTenant();
        app.UseAcme(tenant);
        app.Access.MissingCostCenters["XX-404"] = true;
        using var client = Main.CreateClient();
        var thandi = User(tenant, DemoUsers.Thandi, "finance");

        var preview = await ReadAsync<ApprovalPreviewDto>(await client.SendAsync(Get($"preview?costCenter={CostCenter}&amount=250000", thandi), Ct));
        var unknown = await ReadAsync<ApprovalPreviewDto>(await client.SendAsync(Get("preview?costCenter=XX-404&amount=100", thandi), Ct));
        var badAmount = await client.SendAsync(Get($"preview?costCenter={CostCenter}&amount=0", thandi), Ct);

        // Thandi is the only finance approver and cannot approve her own request.
        preview.Possible.ShouldBeFalse();
        preview.Reason.ShouldBe(new NoEligibleApproverException(ApprovalLevel.Finance).Message);
        preview.Steps.ShouldBeEmpty();
        unknown.Possible.ShouldBeFalse();
        unknown.Reason.ShouldBe(ApprovalSaga.UnknownCostCenter("XX-404"));
        badAmount.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    /// <summary>A request of <paramref name="total"/> raised by <paramref name="requester"/>, with its budget reserved: its chain exists and step 1 is open.</summary>
    private static async Task<(Guid Id, List<ApprovalStep> Steps)> OpenAsync(
        ApprovalsInstance instance, string tenant, string requester, decimal total)
    {
        var submitted = Submitted(tenant, requester, total);
        await instance.InvokeAsync(submitted, tenant, requester);
        await instance.InvokeAsync(Reserved(submitted), tenant, requester);
        return (submitted.RequestId, await instance.StepsAsync(submitted.RequestId));
    }

    private static async Task<(Guid Id, List<ApprovalStep> Steps)[]> OpenManyAsync(ApprovalsInstance instance, string tenant, int count)
    {
        var chains = new List<(Guid, List<ApprovalStep>)>();
        for (var i = 0; i < count; i++)
        {
            chains.Add(await OpenAsync(instance, tenant, DemoUsers.Tom, 5_000m));
        }

        return [.. chains];
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + EvictionTimeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The cached read did not change within {EvictionTimeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private static string Tag(uint version) => $"\"{version}\"";

    /// <summary>How a request authenticates.</summary>
    private sealed record Caller(Action<HttpRequestMessage> Apply);

    private static Caller User(string tenant, string user, string role) =>
        new(r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateToken(user, tenant, role)));

    private static Caller AdminKey(string tenant, string? onBehalfOf = null) =>
        new(r =>
        {
            r.Headers.Add("X-Api-Key", TestAuth.ApiKey);
            r.Headers.Add("X-Tenant-Id", tenant);
            if (onBehalfOf is not null)
            {
                r.Headers.Add("X-On-Behalf-Of-User", onBehalfOf);
            }
        });

    private static Caller Service(string tenant) =>
        new(r =>
        {
            r.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", TestAuth.CreateServiceToken(ApprovalsApiFactory.InternalClientId));
            r.Headers.Add("X-Tenant-Id", tenant);
            r.Headers.Add("X-Calling-Service", "requests-api");
        });

    private static HttpRequestMessage Get(string path, Caller caller)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/{path}");
        caller.Apply(request);
        return request;
    }

    /// <summary>A POST with <c>If-Match</c> (when given) and an <c>Idempotency-Key</c> (a new one unless given; empty sends none).</summary>
    private static HttpRequestMessage Post(string path, Caller caller, string? ifMatch, object? body = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{path}");
        caller.Apply(request);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (key != "")
        {
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}
