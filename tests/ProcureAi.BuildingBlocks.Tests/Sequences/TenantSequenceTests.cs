using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Persistence;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Sequences;

public sealed class TenantSequenceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed class SequenceDbContext(DbContextOptions<SequenceDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddTenantSequences();
    }

    private static SequenceDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<SequenceDbContext>().UseNpgsql(connectionString).Options);

    private async Task<string> NewDatabaseAsync(string name)
    {
        var connectionString = await postgres.CreateDatabaseAsync(name);
        await using var db = NewContext(connectionString);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return connectionString;
    }

    [Fact]
    public async Task Fifty_parallel_calls_on_two_contexts_return_1_to_50_once()
    {
        var connectionString = await NewDatabaseAsync("seq_parallel");
        await using var first = NewContext(connectionString);
        await using var second = NewContext(connectionString);
        var sequences = new[] { new TenantSequence<SequenceDbContext>(first), new TenantSequence<SequenceDbContext>(second) };

        // Two contexts stand for two service instances; each context runs its calls one after the other.
        var tasks = sequences.Select(sequence => Task.Run(async () =>
        {
            var numbers = new List<int>();
            for (var i = 0; i < 25; i++)
            {
                numbers.Add(await sequence.NextAsync("acme", "PR", TestContext.Current.CancellationToken));
            }

            return numbers;
        })).ToArray();
        var all = (await Task.WhenAll(tasks)).SelectMany(n => n).Order().ToList();

        all.ShouldBe(Enumerable.Range(1, 50).ToList());
    }

    [Fact]
    public async Task Tenants_and_names_count_separately()
    {
        var connectionString = await NewDatabaseAsync("seq_separate");
        await using var db = NewContext(connectionString);
        var sequence = new TenantSequence<SequenceDbContext>(db);

        (await sequence.NextAsync("acme", "PR", TestContext.Current.CancellationToken)).ShouldBe(1);
        (await sequence.NextAsync("acme", "PR", TestContext.Current.CancellationToken)).ShouldBe(2);
        (await sequence.NextAsync("acme", "PO", TestContext.Current.CancellationToken)).ShouldBe(1);
        (await sequence.NextAsync("globex", "PR", TestContext.Current.CancellationToken)).ShouldBe(1);
        (await sequence.NextAsync("acme", "PR", TestContext.Current.CancellationToken)).ShouldBe(3);
    }

    [Fact]
    public async Task A_rolled_back_transaction_gives_the_number_back()
    {
        var connectionString = await NewDatabaseAsync("seq_rollback");
        await using var db = NewContext(connectionString);
        var sequence = new TenantSequence<SequenceDbContext>(db);

        await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            (await sequence.NextAsync("acme", "PR", TestContext.Current.CancellationToken)).ShouldBe(1);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await sequence.NextAsync("acme", "PR", TestContext.Current.CancellationToken)).ShouldBe(1);
    }
}
