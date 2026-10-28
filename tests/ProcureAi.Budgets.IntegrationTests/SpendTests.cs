using ProcureAi.Contracts.Budgets;
using Shouldly;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>RecordSpend, ReleaseBudget and a repeated ReserveBudget over the real broker, database and outbox.</summary>
[Collection(BudgetsCollection.Name)]
public sealed class SpendTests(BudgetsApp app)
{
    private const string CostCenter = "CC-100";
    private const decimal Budget = 100_000m;
    private const decimal Reservation = 86_940m;
    private const decimal Payment = 57_960m;
    private static readonly TimeSpan Timeout = BudgetsApp.EventTimeout;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> ReservedRequestAsync(string tenant, string costCenter = CostCenter)
    {
        var id = Guid.NewGuid();
        await app.SendAsync(new ReserveBudget(id, tenant, costCenter, Reservation, DateTimeOffset.UtcNow), tenant);
        await app.Events.WaitForAsync(id, e => e.Any(x => x.Kind == BudgetEventKind.Reserved), Timeout);
        return id;
    }

    private async Task SpendAsync(string tenant, Guid request, Guid payment, decimal amount, bool wait = true)
    {
        await app.SendAsync(new RecordSpend(request, tenant, payment, amount), tenant);
        if (wait)
        {
            await app.Events.WaitForAsync(
                request, e => e.Any(x => x.Kind == BudgetEventKind.Spent && x.Body.GetProperty("paymentId").GetGuid() == payment), Timeout);
        }
    }

    private async Task ReleaseAsync(string tenant, Guid request, int releasedEvents)
    {
        await app.SendAsync(new ReleaseBudget(request, tenant), tenant);
        if (releasedEvents > 0)
        {
            await app.Events.WaitForAsync(request, e => e.Count(x => x.Kind == BudgetEventKind.Released) >= releasedEvents, Timeout);
        }
    }

    [Fact]
    public async Task RecordSpend_publishes_BudgetSpent_and_moves_reserved_to_spent()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, Budget);
        var request = await ReservedRequestAsync(tenant);
        var payment = Guid.NewGuid();

        await SpendAsync(tenant, request, payment, Payment);

        var spent = app.Events.Of(request).Single(e => e.Kind == BudgetEventKind.Spent);
        spent.Body.GetProperty("amount").GetDecimal().ShouldBe(Payment);
        spent.Body.GetProperty("costCenterCode").GetString().ShouldBe(CostCenter);
        spent.TenantId.ShouldBe(tenant);
        var stored = await app.StoredAsync(tenant, CostCenter);
        stored.Reserved.ShouldBe(Reservation - Payment);
        stored.Active.ShouldBe(Reservation - Payment);
        (await app.SpentAsync(tenant, CostCenter)).ShouldBe((Payment, 1L));
        (await app.GetBudgetAsync(tenant, CostCenter)).Available.ShouldBe(Budget - Reservation);
    }

    [Fact]
    public async Task The_same_payment_sent_twice_is_recorded_and_published_once()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, Budget);
        var request = await ReservedRequestAsync(tenant);
        var payment = Guid.NewGuid();
        var deadBefore = await app.DeadLettersAsync();

        await Task.WhenAll(
            SpendAsync(tenant, request, payment, Payment, wait: false), SpendAsync(tenant, request, payment, Payment, wait: false));
        await app.Events.WaitForAsync(request, e => e.Any(x => x.Kind == BudgetEventKind.Spent), Timeout);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        app.Events.Of(request).Count(e => e.Kind == BudgetEventKind.Spent).ShouldBe(1);
        (await app.SpentAsync(tenant, CostCenter)).ShouldBe((Payment, 1L));
        (await app.DeadLettersAsync()).ShouldBe(deadBefore);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Spend_then_release_and_release_then_spend_give_the_same_numbers(bool releaseFirst, bool duplicates)
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, Budget);
        var request = await ReservedRequestAsync(tenant);
        var payment = Guid.NewGuid();
        var deadBefore = await app.DeadLettersAsync();

        if (releaseFirst)
        {
            await ReleaseAsync(tenant, request, releasedEvents: 1);
            await SpendAsync(tenant, request, payment, Payment);
        }
        else
        {
            await SpendAsync(tenant, request, payment, Payment);
            await ReleaseAsync(tenant, request, releasedEvents: 1);
        }

        if (duplicates)
        {
            await ReleaseAsync(tenant, request, releasedEvents: 0);
            await SpendAsync(tenant, request, payment, Payment, wait: false);
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }

        // Spent R 57 960,00, nothing reserved, so R 42 040,00 left, in every order.
        var stored = await app.StoredAsync(tenant, CostCenter);
        stored.Reserved.ShouldBe(0m);
        stored.Active.ShouldBe(0m);
        stored.Available.ShouldBe(Budget - Payment);
        (await app.SpentAsync(tenant, CostCenter)).ShouldBe((Payment, 1L));
        var released = app.Events.Of(request).Where(e => e.Kind == BudgetEventKind.Released).ToList();
        released.Count.ShouldBe(1, "a repeated release is silent");
        released[0].Body.GetProperty("amount").GetDecimal().ShouldBe(releaseFirst ? Reservation : Reservation - Payment);
        app.Events.Of(request).Count(e => e.Kind == BudgetEventKind.Spent).ShouldBe(1);
        (await app.DeadLettersAsync()).ShouldBe(deadBefore);
    }

    [Fact]
    public async Task Concurrent_spends_of_two_payments_both_recorded()
    {
        // Both payments change the one budget row, so they conflict on its version; the loser is retried. Four instances
        // compete for the queue, like replicas in a cluster.
        const int repetitions = 15;
        await using var replicas = await app.StartExtraInstancesAsync(3);
        var tenant = BudgetsInfraFixture.NewTenant();
        var deadBefore = await app.DeadLettersAsync();

        for (var i = 0; i < repetitions; i++)
        {
            var costCenter = $"CC-{i:D2}";
            await app.Infra.CreateBudgetAsync(tenant, costCenter, Budget);
            var request = await ReservedRequestAsync(tenant, costCenter);
            Guid[] payments = [Guid.NewGuid(), Guid.NewGuid()];

            await Task.WhenAll(payments.Select(p => SpendAsync(tenant, request, p, 20_000m, wait: false)));
            await app.Events.WaitForAsync(request, e => e.Count(x => x.Kind == BudgetEventKind.Spent) >= 2, Timeout);

            (await app.SpentAsync(tenant, costCenter)).ShouldBe((40_000m, 2L), $"repetition {i}");
            var stored = await app.StoredAsync(tenant, costCenter);
            stored.Reserved.ShouldBe(Reservation - 40_000m, $"repetition {i}");
            stored.Reserved.ShouldBe(stored.Active);
        }

        (await app.DeadLettersAsync()).ShouldBe(deadBefore, "every loser of a race was retried, none dead-lettered");
    }

    [Fact]
    public async Task Spend_for_a_request_no_budget_ever_held_goes_to_the_error_queue_without_retries()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, Budget);
        var request = Guid.NewGuid();
        var deadBefore = await app.DeadLettersAsync();

        await app.SendAsync(new RecordSpend(request, tenant, Guid.NewGuid(), Payment), tenant);

        var deadline = DateTime.UtcNow + Timeout;
        while (await app.DeadLettersAsync() == deadBefore && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }

        (await app.DeadLettersAsync()).ShouldBe(deadBefore + 1, "moved to the error queue on the first failure");
        app.Events.Of(request).ShouldBeEmpty();
        (await app.SpentAsync(tenant, CostCenter)).ShouldBe((0m, 0L));
    }

    [Fact]
    public async Task Repeated_reserve_answers_from_holding_budget()
    {
        var tenant = BudgetsInfraFixture.NewTenant();
        await app.Infra.CreateBudgetAsync(tenant, CostCenter, Budget);
        await app.Infra.CreateBudgetAsync(tenant, "CC-200", Budget);
        var request = await ReservedRequestAsync(tenant);

        // The repeat names another cost center, another amount and a submission date in the next fiscal year.
        await app.SendAsync(
            new ReserveBudget(request, tenant, "CC-200", 1_000m, DateTimeOffset.UtcNow.AddYears(1)), tenant);
        await app.Events.WaitForAsync(request, e => e.Count >= 2, Timeout);

        var answers = app.Events.Of(request);
        answers.ShouldAllBe(e => e.Kind == BudgetEventKind.Reserved);
        foreach (var answer in answers)
        {
            answer.Body.GetProperty("costCenterCode").GetString().ShouldBe(CostCenter);
            answer.Body.GetProperty("fiscalYear").GetString().ShouldBe(BudgetsInfraFixture.CurrentYear.ToString());
            answer.Body.GetProperty("amount").GetDecimal().ShouldBe(Reservation);
        }

        (await app.StoredAsync(tenant, "CC-200")).Reserved.ShouldBe(0m, "the other cost center was never touched");
        (await app.StoredAsync(tenant, CostCenter)).Reserved.ShouldBe(Reservation);
    }
}
