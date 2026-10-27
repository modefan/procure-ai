using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts.Requests;
using ProcureAi.Requests.Application.Commands;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Queries;
using ProcureAi.Requests.Domain;
using ProcureAi.Requests.Infrastructure;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace ProcureAi.Requests.IntegrationTests;

[Collection(InfraCollection.Name)]
public sealed class HandlerTests(InfraFixture infra) : IAsyncLifetime
{
    private RequestsHost _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await RequestsHost.StartAsync(infra.RequestsDbConnectionString, infra.RabbitConnectionString);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static CreateRequest NewCreate(string title = "Laptops") =>
        new(title, "CC-100", "sup-1", [new LineInput("LAP-1", "Laptop", 3, 25200m)]);

    private Task<RequestDto> Create(CreateRequest? command = null) =>
        _app.Bus.InvokeAsync<RequestDto>(command ?? NewCreate());

    [Fact]
    public async Task Create_then_get_returns_request_with_version()
    {
        _app.Caller.TenantId = "acme";
        var created = await Create();

        var loaded = await _app.Bus.InvokeAsync<RequestDto>(new GetRequest(created.Id));

        loaded.Id.ShouldBe(created.Id);
        loaded.Number.ShouldBe(created.Number);
        loaded.Number.ShouldStartWith("PR-");
        loaded.Title.ShouldBe("Laptops");
        loaded.Status.ShouldBe(RequestStatus.Draft);
        loaded.CostCenterCode.ShouldBe("CC-100");
        loaded.SupplierId.ShouldBe("sup-1");
        loaded.Version.ShouldBeGreaterThan(0u);
        loaded.Version.ShouldBe(created.Version);
        loaded.Lines.ShouldHaveSingleItem().LineNo.ShouldBe(1);
        loaded.TotalExclVat.ShouldBe(75600m);
        loaded.Vat.ShouldBe(11340m);
        loaded.TotalInclVat.ShouldBe(86940m);
        loaded.TotalInclVatDisplay.ShouldBe("R 86 940,00");
    }

    [Fact]
    public async Task A_requester_cannot_create_a_request_on_a_cost_center_they_do_not_act_for()
    {
        _app.Caller.TenantId = "acme";
        _app.Caller.UserId = "tom";
        _app.Access.SetCostCenters("acme", "tom", "IT-200");

        await Should.ThrowAsync<ForbiddenException>(() => Create(NewCreate()));

        _app.Caller.Kind = CallerKind.User;
        var ok = await Create(new CreateRequest("Laptops", "IT-200", "sup-1", [new LineInput("LAP-1", "Laptop", 1, 100m)]));
        ok.CostCenterCode.ShouldBe("IT-200");
    }

    [Fact]
    public async Task A_requester_cannot_submit_a_request_on_a_cost_center_they_do_not_act_for()
    {
        _app.Caller.TenantId = "acme";
        _app.Caller.UserId = "tom";
        _app.Access.SetCostCenters("acme", "tom", "CC-100");
        var created = await Create();
        // Access moves Tom to another cost center; the draft on CC-100 is no longer his to submit.
        _app.Access.SetCostCenters("acme", "tom", "IT-200");

        await Should.ThrowAsync<ForbiddenException>(() => _app.Bus.InvokeAsync(new SubmitRequest(created.Id, created.Version)));

        (await _app.Bus.InvokeAsync<RequestDto>(new GetRequest(created.Id))).Status.ShouldBe(RequestStatus.Draft);
    }

    [Fact]
    public async Task The_admin_api_key_may_create_and_submit_on_any_cost_center()
    {
        _app.Caller.TenantId = "acme";
        _app.Caller.UserId = "claude-admin";
        _app.Caller.Kind = CallerKind.AdminApiKey;
        _app.Access.SetCostCenters("acme", "claude-admin");

        var created = await Create(new CreateRequest("Anything", "ANY-1", "sup-1", [new LineInput("A", "a", 1, 10m)]));
        await _app.Bus.InvokeAsync(new SubmitRequest(created.Id, created.Version));

        (await _app.Bus.InvokeAsync<RequestDto>(new GetRequest(created.Id))).Status.ShouldBe(RequestStatus.Submitted);
    }

    [Fact]
    public async Task Update_changes_title_and_lines_and_bumps_version()
    {
        _app.Caller.TenantId = "acme";
        var created = await Create();

        var updated = await _app.Bus.InvokeAsync<RequestDto>(new UpdateRequest(
            created.Id, created.Version, "Monitors", [new LineInput("MON-1", "Monitor", 2, 3000m)]));

        updated.Title.ShouldBe("Monitors");
        updated.Lines.Select(l => l.Sku).ShouldBe(["MON-1"]);
        updated.TotalExclVat.ShouldBe(6000m);
        updated.Version.ShouldNotBe(created.Version);
    }

    [Fact]
    public async Task Submit_publishes_RequestSubmitted_through_outbox()
    {
        _app.Caller.TenantId = "acme";
        var created = await Create();

        var session = await _app.Host.TrackActivity()
            .ExecuteAndWaitAsync(c => c.InvokeAsync(new SubmitRequest(created.Id, created.Version)));

        var sent = session.Sent.SingleMessage<RequestSubmitted>();
        sent.RequestId.ShouldBe(created.Id);
        sent.TenantId.ShouldBe("acme");
        sent.Number.ShouldBe(created.Number);
        sent.Title.ShouldBe(created.Title);
        sent.TotalInclVat.ShouldBe(86940m);
        var loaded = await _app.Bus.InvokeAsync<RequestDto>(new GetRequest(created.Id));
        loaded.Status.ShouldBe(RequestStatus.Submitted);
    }

    [Fact]
    public async Task Stale_version_throws_ConcurrencyConflictException()
    {
        _app.Caller.TenantId = "acme";
        var created = await Create();
        await _app.Bus.InvokeAsync<RequestDto>(
            new UpdateRequest(created.Id, created.Version, "Monitors", created.Lines.Select(ToInput).ToList()));

        await Should.ThrowAsync<ConcurrencyConflictException>(() =>
            _app.Bus.InvokeAsync<RequestDto>(
                new UpdateRequest(created.Id, created.Version, "Again", created.Lines.Select(ToInput).ToList())));
        await Should.ThrowAsync<ConcurrencyConflictException>(() =>
            _app.Bus.InvokeAsync(new SubmitRequest(created.Id, created.Version)));
    }

    [Fact]
    public async Task Other_tenant_cannot_see_request()
    {
        _app.Caller.TenantId = "acme";
        var created = await Create();

        _app.Caller.TenantId = "globex";
        await Should.ThrowAsync<NotFoundException>(() => _app.Bus.InvokeAsync<RequestDto>(new GetRequest(created.Id)));
        await Should.ThrowAsync<NotFoundException>(() =>
            _app.Bus.InvokeAsync(new SubmitRequest(created.Id, created.Version)));
        var page = await _app.Bus.InvokeAsync<PagedResult<RequestDto>>(new ListRequests(1, 50, null));
        page.Items.ShouldNotContain(r => r.Id == created.Id);
    }

    [Fact]
    public async Task Missing_request_throws_NotFoundException()
    {
        _app.Caller.TenantId = "acme";
        await Should.ThrowAsync<NotFoundException>(() => _app.Bus.InvokeAsync<RequestDto>(new GetRequest(Guid.NewGuid())));
    }

    [Fact]
    public async Task Cannot_submit_a_request_without_lines()
    {
        _app.Caller.TenantId = "acme";
        var created = await Create(new CreateRequest("Empty", "CC-100", "sup-1", []));

        await Should.ThrowAsync<DomainException>(() => _app.Bus.InvokeAsync(new SubmitRequest(created.Id, created.Version)));
    }

    [Fact]
    public async Task List_pages_and_filters_by_status()
    {
        _app.Caller.TenantId = $"list-{Guid.NewGuid():N}";
        var first = await Create(NewCreate("one"));
        await Create(NewCreate("two"));
        await Create(NewCreate("three"));
        await _app.Bus.InvokeAsync(new SubmitRequest(first.Id, first.Version));

        var page1 = await _app.Bus.InvokeAsync<PagedResult<RequestDto>>(new ListRequests(1, 2, null));
        var page2 = await _app.Bus.InvokeAsync<PagedResult<RequestDto>>(new ListRequests(2, 2, null));
        var submitted = await _app.Bus.InvokeAsync<PagedResult<RequestDto>>(new ListRequests(1, 10, RequestStatus.Submitted));

        page1.Total.ShouldBe(3);
        page1.Items.Select(i => i.Title).ShouldBe(["three", "two"]);
        page2.Items.Select(i => i.Title).ShouldBe(["one"]);
        submitted.Items.ShouldHaveSingleItem().Id.ShouldBe(first.Id);
        await Should.ThrowAsync<DomainException>(() =>
            _app.Bus.InvokeAsync<PagedResult<RequestDto>>(new ListRequests(0, 10, null)));
        await Should.ThrowAsync<DomainException>(() =>
            _app.Bus.InvokeAsync<PagedResult<RequestDto>>(new ListRequests(1, 100_000, null)));
    }

    [Fact]
    public async Task Concurrent_creates_in_one_tenant_get_distinct_sequential_numbers()
    {
        _app.Caller.TenantId = $"race-{Guid.NewGuid():N}";

        var created = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Create(NewCreate($"r{i}"))));

        created.Select(c => c.Number).Distinct().Count().ShouldBe(10);
        created.Select(c => RequestNumber.Parse(c.Number).Sequence).Order().ShouldBe(Enumerable.Range(1, 10));
    }

    [Fact]
    public async Task Request_numbers_are_per_tenant()
    {
        _app.Caller.TenantId = $"num-a-{Guid.NewGuid():N}";
        var a = await Create();
        _app.Caller.TenantId = $"num-b-{Guid.NewGuid():N}";
        var b = await Create();

        a.Number.ShouldBe("PR-0001");
        b.Number.ShouldBe("PR-0001");
    }

    [Fact]
    public async Task Failed_create_gives_its_number_back()
    {
        // The number is taken inside the handler's transaction, so a create that fails leaves no gap.
        _app.Caller.TenantId = $"gap-{Guid.NewGuid():N}";

        await Should.ThrowAsync<DomainException>(() => Create(
            new CreateRequest("Bad", "CC-100", "sup-1", [new LineInput("A", "a", 0, 1m)])));
        var created = await Create();

        created.Number.ShouldBe("PR-0001");
    }

    [Fact]
    public async Task Message_handler_audits_service_as_actor()
    {
        // The real caller (the API's CallerContext), not the settable test caller: user and tenant come from the headers.
        await using var app = await RequestsHost.StartAsync(
            infra.RequestsDbConnectionString, infra.RabbitConnectionString, useRealCaller: true);
        var tenant = $"msg-{Guid.NewGuid():N}";
        var delivery = new DeliveryOptions();
        delivery.Headers[CallerHeaders.UserId] = "tom";
        delivery.Headers[CallerHeaders.TenantId] = tenant;
        delivery.Headers[CallerHeaders.CallingService] = "approvals-api";

        var created = await app.Bus.InvokeAsync<RequestDto>(NewCreate(), delivery);

        await using var db = new RequestsDbContext(
            new DbContextOptionsBuilder<RequestsDbContext>().UseNpgsql(infra.RequestsDbConnectionString).Options);
        var audit = await db.Set<AuditEntry>()
            .Where(e => e.Table == "purchase_requests" && e.PrimaryKey == created.Id.ToString())
            .SingleAsync();
        audit.UserId.ShouldBe("tom");
        audit.ActorId.ShouldBe("service:requests-api");
        audit.TenantId.ShouldBe(tenant);
        // A consumer reads the sender of the message as the calling service.
        audit.CallingService.ShouldBe("approvals-api");
    }

    [Fact]
    public async Task Message_survives_broker_outage()
    {
        // Own RabbitMQ and own database, so stopping the broker cannot disturb other tests.
        await using var rabbit = await RabbitContainer.StartAsync();
        var database = await infra.CreateDatabaseAsync($"outage_{Guid.NewGuid():N}");
        await using var app = await RequestsHost.StartAsync(database, rabbit.ConnectionString);
        app.Caller.TenantId = "acme";
        var created = await app.Bus.InvokeAsync<RequestDto>(NewCreate());
        await rabbit.BindQueueAsync("outage-test", "requests");

        await rabbit.StopAsync();
        await app.Bus.InvokeAsync(new SubmitRequest(created.Id, created.Version));

        // The business transaction committed and the message waits in Wolverine's outbox.
        (await CountOutgoingAsync(database)).ShouldBeGreaterThan(0);

        await rabbit.ResumeAsync();
        var delivered = await rabbit.WaitForMessageAsync("outage-test", TimeSpan.FromSeconds(30));

        delivered.ShouldContain(created.Id.ToString());
    }

    private static LineInput ToInput(RequestLineDto l) => new(l.Sku, l.Description, l.Quantity, l.UnitPriceExclVat);

    private static async Task<long> CountOutgoingAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM wolverine.wolverine_outgoing_envelopes", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
