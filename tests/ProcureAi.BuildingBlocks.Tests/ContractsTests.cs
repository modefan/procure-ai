using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests;

public sealed class ContractsTests
{
    private static readonly string[] Commands = ["ReserveBudget", "ReleaseBudget"];

    private static readonly string[] Events =
    [
        "RequestSubmitted", "RequestCancelled",
        "BudgetReserved", "BudgetReservationFailed", "BudgetReleased",
        "ApprovalRequested", "ApprovalStepApproved", "ApprovalReminderDue", "ApprovalEscalated", "ApprovalAlertRaised",
        "RequestApproved", "RequestRejected",
        "AccessChanged",
    ];

    private static Type[] ContractTypes() =>
        typeof(IIntegrationEvent).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Namespace?.StartsWith("ProcureAi.Contracts.", StringComparison.Ordinal) == true)
            .ToArray();

    [Fact]
    public void Every_event_implements_IIntegrationEvent_and_commands_do_not()
    {
        var types = ContractTypes();

        types.Select(t => t.Name).Order().ShouldBe(Commands.Concat(Events).Order());
        foreach (var type in types)
        {
            typeof(IIntegrationEvent).IsAssignableFrom(type).ShouldBe(Events.Contains(type.Name), type.Name);
        }
    }

    [Fact]
    public void Every_contract_names_the_tenant_and_uses_no_floating_point()
    {
        foreach (var type in ContractTypes())
        {
            type.GetProperty("TenantId")!.PropertyType.ShouldBe(typeof(string), type.Name);
            type.GetProperties().Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(float))
                .ShouldBeEmpty(type.Name);
        }
    }

    [Fact]
    public void Exchanges_and_queues_have_the_agreed_names()
    {
        Exchanges.Requests.ShouldBe("requests");
        Exchanges.Budgets.ShouldBe("budgets");
        Exchanges.Approvals.ShouldBe("approvals");
        Exchanges.Access.ShouldBe("access");
        Queues.BudgetCommands.ShouldBe("budgets-commands");
    }

    [Fact]
    public void Demo_users_have_fixed_ids_in_the_agreed_pattern()
    {
        DemoUsers.Tom.ShouldBe("00000000-0000-4000-8000-000000000001");
        DemoUsers.Ben.ShouldBe("00000000-0000-4000-8000-000000000006");
        DemoUsers.All.Count.ShouldBe(6);
        DemoUsers.All.Values.Distinct().Count().ShouldBe(6);
    }
}
