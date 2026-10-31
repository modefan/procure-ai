using System.Net;
using ProcureAi.Contracts;
using Shouldly;
using static ProcureAi.Approvals.IntegrationTests.ApprovalsApp;

namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>
/// The nightly demo reset of Approvals (plan 06 rulings 4 and 5) on an instance with a database of its own: the steps and sagas of every
/// tenant are deleted with ordinary deletes the interceptor records, and the scheduled saga timeouts go with Wolverine's envelopes.
/// </summary>
[Collection(ApprovalsCollection.Name)]
public sealed class DemoResetTests(ApprovalsApp app)
{
    [Fact]
    public async Task Demo_reset_clears_business_rows_and_reseeds()
    {
        await using var instance = await app.StartFastInstanceAsync(new Dictionary<string, string?>(ManualTimeouts) { ["DemoReset:Enabled"] = "true" });
        var ids = new List<Guid>();
        foreach (var tenant in new[] { NewTenant(), NewTenant() })
        {
            app.UseAcme(tenant);
            var submitted = Submitted(tenant, DemoUsers.Tom, 30_000m);
            await instance.InvokeAsync(submitted, tenant, DemoUsers.Tom);
            await instance.InvokeAsync(Reserved(submitted), tenant, DemoUsers.Tom);
            (await instance.StepsAsync(submitted.RequestId)).ShouldNotBeEmpty();
            ids.Add(submitted.RequestId);
        }

        (await DemoResetProbe.CountAsync(instance.DbConnectionString, "select count(*) from wolverine.wolverine_incoming_envelopes where status = 'Scheduled'"))
            .ShouldBeGreaterThan(0, "the open steps' reminders are scheduled");

        var steps = await DemoResetProbe.CountAsync(instance.DbConnectionString, "select count(*) from approval_steps");
        using var client = instance.CreateClient();
        (await DemoResetProbe.ResetAsync(client, "approvals")).ShouldBe(HttpStatusCode.NoContent);

        foreach (var id in ids)
        {
            (await instance.StepsAsync(id)).ShouldBeEmpty();
            (await instance.SagaAsync(id)).ShouldBeNull();
        }

        (await DemoResetProbe.CountAsync(instance.DbConnectionString, "select count(*) from approval_steps")).ShouldBe(0);
        (await DemoResetProbe.CountAsync(instance.DbConnectionString, "select count(*) from wolverine.wolverine_incoming_envelopes where status = 'Scheduled'"))
            .ShouldBe(0, "no reminder or escalation of a deleted step may fire");
        var deletes = await DemoResetProbe.DeleteEntriesAsync(instance.DbConnectionString, "approval_steps");
        deletes.Count.ShouldBe((int)steps, "one interceptor row per deleted step (ruling 5)");
        deletes.Select(d => d.TransactionId).Distinct().ShouldHaveSingleItem();

        (await DemoResetProbe.ResetAsync(client, "approvals")).ShouldBe(HttpStatusCode.NoContent, "twice is safe");
    }
}
