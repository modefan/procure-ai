using ProcureAi.Approvals.Domain;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.DemoData.Tests;

/// <summary>
/// The scenario files (plan 06 rulings 2 and 3) against the seeds and the approval routing: every user, cost center and SKU exists, every
/// approver is the one Approvals will ask, and the stories the demo tells (separation of duties, PO-1001 for the invoice scan) are in them.
/// </summary>
public sealed class ScenarioFileTests
{
    [Fact]
    public void Scenario_files_reference_existing_skus_users_and_cost_centers()
    {
        DemoWorld.Scenarios.Select(s => s.Tenant).Order().ShouldBe(["acme", "globex"]);
        foreach (var scenario in DemoWorld.Scenarios)
        {
            var tenant = scenario.Tenant;
            DemoWorld.User(tenant, scenario.Finance).ShouldNotBeNull($"{tenant}: finance {scenario.Finance} is not a user of the tenant")
                .IsFinanceApprover.ShouldBeTrue($"{tenant}: {scenario.Finance} is no finance approver");
            foreach (var request in scenario.Requests)
            {
                var what = $"{tenant}/{request.Key}";
                var requester = DemoWorld.User(tenant, request.Requester).ShouldNotBeNull($"{what}: requester {request.Requester} is not in {tenant}");
                foreach (var approver in request.Approvers ?? [])
                {
                    DemoWorld.User(tenant, approver).ShouldNotBeNull($"{what}: approver {approver} is not in {tenant}");
                }

                var costCenter = DemoWorld.CostCenter(tenant, request.CostCenter).ShouldNotBeNull($"{what}: no cost center {request.CostCenter}");
                DemoWorld.ActsFor(requester, costCenter).ShouldBeTrue($"{what}: {request.Requester} does not act for {request.CostCenter}");

                var supplier = DemoWorld.Supplier(tenant, request.Supplier).ShouldNotBeNull($"{what}: no supplier {request.Supplier}");
                foreach (var line in request.Lines.Concat(request.Receive ?? []))
                {
                    supplier.Find(line.Sku).ShouldNotBeNull($"{what}: {request.Supplier} has no {line.Sku}").Active.ShouldBeTrue($"{what}: {line.Sku} is inactive");
                }
            }
        }
    }

    [Fact]
    public void Acme_shows_every_state_and_globex_its_four()
    {
        DemoWorld.Acme.Requests.Select(r => r.StopAt).Distinct().Order().ShouldBe(Enum.GetValues<StopAt>());
        DemoWorld.Acme.Requests.Count(r => r.StopAt == StopAt.Rejected).ShouldBe(2, "rejected by the manager, and over budget");
        DemoWorld.Globex.Requests.Select(r => r.StopAt).ShouldBe([StopAt.Submitted, StopAt.Ordered, StopAt.Completed, StopAt.Rejected], ignoreOrder: true);
        DemoWorld.Globex.Requests.ShouldAllBe(r => r.Requester == "ben@globex.test");
    }

    [Fact]
    public void Approvers_are_the_ones_the_approval_routing_asks()
    {
        foreach (var scenario in DemoWorld.Scenarios)
        {
            foreach (var request in scenario.Requests.Where(r => r.StopAt != StopAt.Draft))
            {
                var what = $"{scenario.Tenant}/{request.Key}";
                var approvers = request.Approvers ?? [];
                var chain = DemoWorld.Chain(scenario.Tenant, request);
                approvers.Count.ShouldBeLessThanOrEqualTo(chain.Steps.Count, what);
                for (var i = 0; i < approvers.Count; i++)
                {
                    chain.Steps[i].ApproverIds.ShouldContain(DemoWorld.UserId(approvers[i]), $"{what}: step {i + 1} is not {approvers[i]}'s");
                }

                switch (request.StopAt)
                {
                    case StopAt.AwaitingDepartmentHead or StopAt.AwaitingFinance:
                        var level = request.StopAt == StopAt.AwaitingFinance ? ApprovalLevel.Finance : ApprovalLevel.DepartmentHead;
                        chain.Steps.Count.ShouldBeGreaterThan(approvers.Count, what);
                        chain.Steps[approvers.Count].Level.ShouldBe(level, $"{what}: the step it waits at");
                        break;
                    case StopAt.Rejected when request.RejectReason is null:
                        // Rejected by Budgets: the reservation does not fit.
                        DemoWorld.TotalInclVat(scenario.Tenant, request).Amount
                            .ShouldBeGreaterThan(DemoWorld.Budget(scenario.Tenant, request.CostCenter), $"{what}: must be over budget");
                        approvers.ShouldBeEmpty(what);
                        break;
                    case StopAt.Rejected:
                        approvers.Count.ShouldBeGreaterThan(0, what);
                        break;
                    case >= StopAt.Ordered:
                        approvers.Count.ShouldBe(chain.Steps.Count, $"{what}: every step is approved");
                        break;
                }

                if (request.StopAt != StopAt.Rejected || request.RejectReason is not null)
                {
                    DemoWorld.TotalInclVat(scenario.Tenant, request).Amount
                        .ShouldBeLessThanOrEqualTo(DemoWorld.Budget(scenario.Tenant, request.CostCenter), $"{what}: fits the budget");
                }
            }

            // Everything that keeps its reservation fits the cost center's budget together.
            foreach (var costCenter in scenario.Requests.GroupBy(r => r.CostCenter))
            {
                var reserved = costCenter.Where(r => r.StopAt is not (StopAt.Draft or StopAt.Rejected or StopAt.Cancelled))
                    .Sum(r => DemoWorld.TotalInclVat(scenario.Tenant, r).Amount);
                reserved.ShouldBeLessThan(DemoWorld.Budget(scenario.Tenant, costCenter.Key) / 2, $"{scenario.Tenant}/{costCenter.Key} keeps room for the reviewers");
            }
        }
    }

    /// <summary>
    /// The separation-of-duties demo (wireframe 16, "Can Maria approve PR-1039?"): Maria raised a request on IT-200, a cost center she
    /// manages, and it waits for approval. The check answers no for exactly one reason: she is the requester.
    /// </summary>
    [Fact]
    public void Pr_1039_scenario_is_maria_requesting_and_denied_to_maria_by_separation_of_duties()
    {
        var request = DemoWorld.Acme.Requests.Single(r => r.Key == "maria-own-request");
        request.Requester.ShouldBe("maria@acme.test");
        request.CostCenter.ShouldBe("IT-200");
        request.StopAt.ShouldBe(StopAt.Submitted);
        DemoWorld.CostCenter("acme", "IT-200")!.ManagerUserId.ShouldBe(DemoUsers.Maria);

        var org = DemoWorld.Organisation("acme", request.CostCenter);
        var facts = new RequestFacts(DemoWorld.TotalInclVat("acme", request), DemoUsers.Maria, org);
        var reasons = ApproverEligibility.Check(ApprovalLevel.Manager, org.Manager!, facts);
        reasons.Where(r => !r.Passed).Select(r => r.Code).ShouldBe([ApproverEligibility.NotRequester]);

        // So the step it waits at is someone else's.
        DemoWorld.Chain("acme", request).Steps[0].ApproverIds.ShouldNotContain(DemoUsers.Maria);
    }

    /// <summary>
    /// Plan 06 ruling 3: Acme's first order is Fabrikam's dock and stand, so it is PO-1001, received in full and not invoiced (Fabrikam
    /// posts PDF invoices): the README's sample invoice and the end-to-end scan test name it.
    /// </summary>
    [Fact]
    public void First_ordered_acme_request_is_fabrikam_dock_and_stand()
    {
        var first = DemoWorld.Acme.Requests.First(r => r.IsOrdered);
        first.Supplier.ShouldBe("fabrikam-office");
        first.Lines.Select(l => l.Sku).ShouldBe(["DOCK-USB", "STAND-LP"]);
        first.StopAt.ShouldBe(StopAt.Received);
        first.Receive.ShouldBeNull("received in full");
        first.Invoice.ShouldBeNull("the invoice is the one the reviewer scans");
        Scenarios.ManualInvoiceSuppliers.ShouldContain(first.Supplier);
    }
}
