using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcureAi.Budgets.Domain;
using ProcureAi.Contracts.Budgets;
using Shouldly;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>ReserveBudget and ReleaseBudget over the real broker, database and outbox.</summary>
[Collection(BudgetsCollection.Name)]
public sealed class ReservationTests(BudgetsApp app)
{
    private const string CostCenter = "CC-100";
    private const int Repetitions = 20;
    private static readonly TimeSpan Timeout = BudgetsApp.EventTimeout;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReserveBudget Reserve(Guid id, string tenant, decimal amount, string costCenter = CostCenter) =>
        new(id, tenant, costCenter, amount, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Reserve_publishes_BudgetReserved_and_lowers_available()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, 100_000m);
        var id = Guid.NewGuid();

        await app.SendAsync(Reserve(id, tenant, 30_000m), tenant);

        var events = await app.Events.WaitForAsync(id, e => e.Count > 0, Timeout);
        var reserved = events.ShouldHaveSingleItem();
        reserved.Kind.ShouldBe(BudgetEventKind.Reserved, "published as BudgetReserved, routed by its runtime type to the budgets exchange");
        reserved.Body.GetProperty("amount").GetDecimal().ShouldBe(30_000m);
        reserved.Body.GetProperty("fiscalYear").GetString().ShouldBe(BudgetsInfraFixture.CurrentYear.ToString());
        reserved.Body.GetProperty("costCenterCode").GetString().ShouldBe(CostCenter);
        var budget = await app.GetBudgetAsync(tenant, CostCenter);
        budget.Reserved.ShouldBe(30_000m);
        budget.Available.ShouldBe(70_000m);
    }

    [Fact]
    public async Task Reserve_over_budget_publishes_BudgetReservationFailed_with_available()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, 1_000m);
        var id = Guid.NewGuid();

        await app.SendAsync(Reserve(id, tenant, 1_000.01m), tenant);

        var failed = (await app.Events.WaitForAsync(id, e => e.Count > 0, Timeout)).ShouldHaveSingleItem();
        failed.Kind.ShouldBe(BudgetEventKind.Failed);
        failed.Reason.ShouldBe("Over budget");
        failed.Body.GetProperty("available").GetDecimal().ShouldBe(1_000m);
    }

    [Theory]
    [InlineData(-50)]
    [InlineData(0)]
    public async Task Reserve_with_an_amount_that_is_not_positive_publishes_a_failure_and_never_dead_letters(int amount)
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, 1_000m);
        var id = Guid.NewGuid();
        var deadBefore = await app.DeadLettersAsync();

        await app.SendAsync(Reserve(id, tenant, amount), tenant);

        var failed = (await app.Events.WaitForAsync(id, e => e.Count > 0, Timeout)).ShouldHaveSingleItem();
        failed.Kind.ShouldBe(BudgetEventKind.Failed);
        failed.Reason.ShouldBe("Amount must be greater than zero");
        (await app.DeadLettersAsync()).ShouldBe(deadBefore);
        (await app.StoredAsync(tenant, CostCenter)).Reserved.ShouldBe(0m);
    }

    [Fact]
    public async Task Concurrent_reservations_that_together_exceed_budget_accept_exactly_one()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        var deadBefore = await app.DeadLettersAsync();

        for (var i = 0; i < Repetitions; i++)
        {
            var costCenter = $"CC-{i:D2}";
            await app.Infra.CreateBudgetAsync(tenant, costCenter, 100_000m);
            Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];

            await Task.WhenAll(ids.Select(id => app.SendAsync(Reserve(id, tenant, 60_000m, costCenter), tenant)));

            var outcomes = new List<BudgetEvent>();
            foreach (var id in ids)
            {
                outcomes.AddRange(await app.Events.WaitForAsync(id, e => e.Count > 0, Timeout));
            }

            outcomes.Count(e => e.Kind == BudgetEventKind.Reserved).ShouldBe(1, $"repetition {i}");
            outcomes.Count(e => e.Kind == BudgetEventKind.Failed).ShouldBe(1, $"repetition {i}");
            outcomes.Single(e => e.Kind == BudgetEventKind.Failed).Reason.ShouldBe("Over budget");
            var stored = await app.StoredAsync(tenant, costCenter);
            stored.Available.ShouldBe(40_000m, $"repetition {i}");
            stored.Reserved.ShouldBe(60_000m);
            stored.Reserved.ShouldBe(stored.Active, "the stored total equals the sum of the active reservations");
        }

        (await app.DeadLettersAsync()).ShouldBe(deadBefore, "every loser of a race was retried, none dead-lettered");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    public async Task Parallel_reservations_on_one_budget_accept_exactly_the_ones_that_fit_and_dead_letter_nothing(int contenders)
    {
        // Four instances compete for the queue, like replicas in a cluster.
        await using var replicas = await app.StartExtraInstancesAsync(3);
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, 100_000m);
        var deadBefore = await app.DeadLettersAsync();
        Guid[] ids = [.. Enumerable.Range(0, contenders).Select(_ => Guid.NewGuid())];

        await Task.WhenAll(ids.Select(id => app.SendAsync(Reserve(id, tenant, 30_000m), tenant)));

        var outcomes = new List<BudgetEvent>();
        foreach (var id in ids)
        {
            outcomes.AddRange(await app.Events.WaitForAsync(id, e => e.Count > 0, Timeout));
        }

        outcomes.Count(e => e.Kind == BudgetEventKind.Reserved).ShouldBe(3, "floor(100 000 / 30 000)");
        var failed = outcomes.Where(e => e.Kind == BudgetEventKind.Failed).ToList();
        failed.Count.ShouldBe(contenders - 3);
        failed.ShouldAllBe(e => e.Reason == "Over budget");
        var stored = await app.StoredAsync(tenant, CostCenter);
        stored.Reserved.ShouldBe(90_000m);
        stored.Reserved.ShouldBe(stored.Active, "the stored total equals the sum of the active reservations");
        (await app.DeadLettersAsync()).ShouldBe(deadBefore, "no contender ran out of retries");
    }

    [Fact]
    public async Task A_burst_of_reservations_that_all_fit_reserves_every_one_and_dead_letters_nothing()
    {
        // Every one of these writes the budget row, so every contender of the burst conflicts with the others (a refusal writes
        // nothing). Four instances compete for the queue, like replicas in a cluster.
        const int contenders = 20;
        await using var replicas = await app.StartExtraInstancesAsync(3);
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, 100_000m);
        var deadBefore = await app.DeadLettersAsync();
        Guid[] ids = [.. Enumerable.Range(0, contenders).Select(_ => Guid.NewGuid())];

        await Task.WhenAll(ids.Select(id => app.SendAsync(Reserve(id, tenant, 1_000m), tenant)));

        var outcomes = new List<BudgetEvent>();
        foreach (var id in ids)
        {
            outcomes.AddRange(await app.Events.WaitForAsync(id, e => e.Count > 0, TimeSpan.FromSeconds(90)));
        }

        outcomes.ShouldAllBe(e => e.Kind == BudgetEventKind.Reserved);
        outcomes.Count.ShouldBe(contenders);
        var stored = await app.StoredAsync(tenant, CostCenter);
        stored.Reserved.ShouldBe(20_000m);
        stored.Reserved.ShouldBe(stored.Active, "the stored total equals the sum of the active reservations");
        (await app.DeadLettersAsync()).ShouldBe(deadBefore, "no contender ran out of retries");
    }

    [Fact]
    public async Task Duplicate_ReserveBudget_message_reserves_once()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, 100_000m);
        var id = Guid.NewGuid();

        await Task.WhenAll(app.SendAsync(Reserve(id, tenant, 25_000m), tenant), app.SendAsync(Reserve(id, tenant, 25_000m), tenant));
        await app.Events.WaitForAsync(id, e => e.Count >= 2, Timeout);

        app.Events.Of(id).ShouldAllBe(e => e.Kind == BudgetEventKind.Reserved, "a repeated message gets the same answer");
        var stored = await app.StoredAsync(tenant, CostCenter);
        stored.Reserved.ShouldBe(25_000m, "reserved once");
        stored.Available.ShouldBe(75_000m);
        stored.Active.ShouldBe(25_000m);
    }

    [Fact]
    public async Task Release_frees_the_reservation_publishes_BudgetReleased_and_a_repeat_is_silent()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, 100_000m);
        var id = Guid.NewGuid();
        await app.SendAsync(Reserve(id, tenant, 40_000m), tenant);
        await app.Events.WaitForAsync(id, e => e.Count >= 1, Timeout);

        await app.SendAsync(new ReleaseBudget(id, tenant), tenant);
        await app.Events.WaitForAsync(id, e => e.Any(x => x.Kind == BudgetEventKind.Released), Timeout);
        await app.SendAsync(new ReleaseBudget(id, tenant), tenant);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        app.Events.Of(id).Count(e => e.Kind == BudgetEventKind.Released).ShouldBe(1);
        var released = app.Events.Of(id).Single(e => e.Kind == BudgetEventKind.Released);
        released.Body.GetProperty("amount").GetDecimal().ShouldBe(40_000m);
        var stored = await app.StoredAsync(tenant, CostCenter);
        stored.Reserved.ShouldBe(0m);
        stored.Available.ShouldBe(100_000m);
    }

    [Fact]
    public async Task Release_then_late_reserve_is_refused_cancelled()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, 100_000m);
        var id = Guid.NewGuid();

        await app.SendAsync(new ReleaseBudget(id, tenant), tenant);
        await WaitForReleasedMarkerAsync(tenant, id);
        await app.SendAsync(Reserve(id, tenant, 10_000m), tenant);

        var failed = (await app.Events.WaitForAsync(id, e => e.Count > 0, Timeout)).ShouldHaveSingleItem();
        failed.Kind.ShouldBe(BudgetEventKind.Failed);
        failed.Reason.ShouldBe("Cancelled");
        (await app.StoredAsync(tenant, CostCenter)).Available.ShouldBe(100_000m);
    }

    [Fact]
    public async Task Concurrent_reserve_and_release_of_one_request_never_leave_a_reservation_behind()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        var deadBefore = await app.DeadLettersAsync();
        var releaseFirst = 0;
        var reserveFirst = 0;

        for (var i = 0; i < Repetitions; i++)
        {
            var costCenter = $"CC-{i:D2}";
            await app.Infra.CreateBudgetAsync(tenant, costCenter, 100_000m);
            var id = Guid.NewGuid();

            await Task.WhenAll(
                app.SendAsync(Reserve(id, tenant, 60_000m, costCenter), tenant), app.SendAsync(new ReleaseBudget(id, tenant), tenant));

            // Either the release won (the reservation is refused as cancelled) or the reservation won and was then released.
            var events = await app.Events.WaitForAsync(
                id,
                e => e.Any(x => x is { Kind: BudgetEventKind.Failed, Reason: "Cancelled" })
                     || (e.Any(x => x.Kind == BudgetEventKind.Reserved) && e.Any(x => x.Kind == BudgetEventKind.Released)),
                Timeout);
            if (events.Any(e => e.Kind == BudgetEventKind.Reserved))
            {
                reserveFirst++;
            }
            else
            {
                releaseFirst++;
            }

            await WaitForReleasedMarkerAsync(tenant, id);
            var stored = await app.StoredAsync(tenant, costCenter);
            stored.Reserved.ShouldBe(0m, $"repetition {i}: no reservation may survive its release");
            stored.Active.ShouldBe(0m, $"repetition {i}");
            stored.Available.ShouldBe(100_000m, $"repetition {i}");
        }

        (await app.DeadLettersAsync()).ShouldBe(deadBefore);
        Console.WriteLine($"reserve/release race over {Repetitions} runs: release first {releaseFirst}, reserve first {reserveFirst}");
    }

    [Fact]
    public async Task Demo_seed_creates_the_demo_budgets_for_the_current_and_next_fiscal_year()
    {
        await using var db = app.Infra.NewDbContext();
        var current = BudgetsInfraFixture.CurrentYear;
        var seeded = (await db.Budgets.IgnoreQueryFilters().Where(b => b.TenantId == "acme" || b.TenantId == "globex").ToListAsync(Ct))
            .ToDictionary(b => (b.TenantId, b.CostCenterCode, b.FiscalYear), b => b.Amount.Amount);

        seeded[("acme", "IT-200", current)].ShouldBe(900_000m);
        seeded[("acme", "MK-110", current)].ShouldBe(360_000m);
        seeded[("globex", "GX-100", current)].ShouldBe(500_000m);
        seeded[("acme", "IT-200", new FiscalYear(current.EndYear + 1))].ShouldBe(900_000m);
        seeded.Count.ShouldBe(6);
    }

    private async Task WaitForReleasedMarkerAsync(string tenant, Guid id)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(app.Infra.DbConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM request_markers WHERE tenant_id = @t AND request_id = @r AND is_released", connection);
            command.Parameters.AddWithValue("t", tenant);
            command.Parameters.AddWithValue("r", id);
            if ((long)(await command.ExecuteScalarAsync(Ct))! == 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }

        throw new TimeoutException($"Request {id} was not marked released.");
    }
}
