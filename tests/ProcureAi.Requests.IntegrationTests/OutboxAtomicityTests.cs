using Npgsql;
using ProcureAi.Requests.Application;
using ProcureAi.Requests.Application.Commands;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Domain;
using Shouldly;
using Wolverine;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>The state change and the outgoing message commit together or not at all.</summary>
[Collection(InfraCollection.Name)]
public sealed class OutboxAtomicityTests(InfraFixture infra) : IAsyncLifetime
{
    private string _database = "";
    private RequestsHost _app = null!;

    public async ValueTask InitializeAsync()
    {
        // Own database, so the outgoing-envelope count is not disturbed by other tests.
        _database = await infra.CreateDatabaseAsync($"atomic_{Guid.NewGuid():N}");
        _app = await RequestsHost.StartAsync(
            _database, infra.RabbitConnectionString,
            options => options.Discovery.IncludeAssembly(typeof(OutboxAtomicityTests).Assembly));
        _app.Caller.TenantId = "acme";
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private Task<RequestDto> CreateAsync() => _app.Bus.InvokeAsync<RequestDto>(
        new CreateRequest("Laptops", "CC-100", "contoso-hardware", [new LineInput("LAP-1", 3)]));

    [Fact]
    public async Task Handler_that_publishes_and_then_fails_leaves_no_message_and_no_state_change()
    {
        var created = await CreateAsync();
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync("requests");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _app.Bus.InvokeAsync(new PublishThenMaybeFail(created.Id, Fail: true)));
        await Task.Delay(TimeSpan.FromSeconds(2));

        (await CountOutgoingAsync()).ShouldBe(0);
        (await probe.CountAsync(queue)).ShouldBe(0u);
        var loaded = await _app.Bus.InvokeAsync<RequestDto>(new Application.Queries.GetRequest(created.Id));
        loaded.Status.ShouldBe(RequestStatus.Draft);
        loaded.Version.ShouldBe(created.Version);
        loaded.SubmittedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Same_handler_without_the_failure_delivers_the_message_and_changes_state()
    {
        // The control: the test setup does see messages and state changes when the handler succeeds.
        var created = await CreateAsync();
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync("requests");

        await _app.Bus.InvokeAsync(new PublishThenMaybeFail(created.Id, Fail: false));

        await probe.WaitForCountAsync(queue, 1, TimeSpan.FromSeconds(30));
        var loaded = await _app.Bus.InvokeAsync<RequestDto>(new Application.Queries.GetRequest(created.Id));
        loaded.Status.ShouldBe(RequestStatus.Submitted);
    }

    private async Task<long> CountOutgoingAsync()
    {
        await using var connection = new NpgsqlConnection(_database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM wolverine.wolverine_outgoing_envelopes", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
