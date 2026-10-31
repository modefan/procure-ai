using System.Net;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Audit.Application.Cdc;
using ProcureAi.Audit.Domain;
using RabbitMQ.Client;
using Shouldly;
using Wolverine;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// The nightly demo reset of Audit (plan 06 rulings 4 and 5), called last: every document (business events, data changes, matches, alerts),
/// Wolverine's envelopes (the pending attribution timeouts) and the broker's shared dead-letter queue are cleared. On a database of its own.
/// </summary>
[Collection(AuditCollection.Name)]
public sealed class DemoResetTests(AuditApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Demo_reset_clears_business_rows_and_reseeds()
    {
        var database = await app.Infra.CreateMigratedDatabaseAsync($"reset_{Guid.NewGuid():N}");
        await using var factory = app.CreateHost(
            _ => { }, new Dictionary<string, string?> { ["ConnectionStrings:auditdb"] = database, ["DemoReset:Enabled"] = "true" });
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var now = DateTimeOffset.UtcNow;
        await using (var session = store.LightweightSession())
        {
            session.Store(new BusinessEvent(Guid.NewGuid(), "acme", "RequestSubmitted", Guid.NewGuid(), "tom", null, "requests", now, "{}"));
            session.Store(new OutsideAppAlert("requestsdb:1:purchase_requests:x", "requestsdb", "purchase_requests", "x", "acme", 1, now, now, null));
            await session.SaveChangesAsync(Ct);
        }

        await factory.Services.GetRequiredService<IMessageBus>().ScheduleAsync(new ResolveAttribution("a-change"), TimeSpan.FromHours(1));
        (await DemoResetProbe.CountAsync(database, "select count(*) from wolverine.wolverine_incoming_envelopes")).ShouldBeGreaterThan(0);
        await DeadLetterAsync();

        (await DemoResetProbe.ResetAsync(client, "audit")).ShouldBe(HttpStatusCode.NoContent);

        await using (var session = store.QuerySession())
        {
            (await session.Query<BusinessEvent>().CountAsync(Ct)).ShouldBe(0);
            (await session.Query<OutsideAppAlert>().CountAsync(Ct)).ShouldBe(0);
        }

        (await DemoResetProbe.EnvelopesAsync(database)).ShouldBe(0, "no attribution timeout of a cleared change may fire");
        (await DeadLettersOnBrokerAsync()).ShouldBe(0u);
        (await DemoResetProbe.ResetAsync(client, "audit")).ShouldBe(HttpStatusCode.NoContent, "twice is safe");
    }

    private const string DeadLetterQueue = "wolverine-dead-letter-queue";

    private async Task DeadLetterAsync()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(app.Infra.RabbitConnectionString) }.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: Ct);
        await channel.BasicPublishAsync("", DeadLetterQueue, "a dead letter"u8.ToArray(), Ct);
        (await DeadLettersOnBrokerAsync()).ShouldBeGreaterThan(0u);
    }

    private async Task<uint> DeadLettersOnBrokerAsync()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(app.Infra.RabbitConnectionString) }.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        for (var i = 0; i < 20; i++)
        {
            var depth = (await channel.QueueDeclarePassiveAsync(DeadLetterQueue, Ct)).MessageCount;
            if (depth > 0 || i == 19)
            {
                return depth;
            }

            await Task.Delay(100, Ct); // a publish takes a moment to show in the depth
        }

        return 0;
    }
}
