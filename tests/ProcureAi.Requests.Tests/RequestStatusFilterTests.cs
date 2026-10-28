using ProcureAi.Requests.Application.Queries;
using ProcureAi.Requests.Domain;
using Shouldly;

namespace ProcureAi.Requests.Tests;

public sealed class RequestStatusFilterTests
{
    [Fact]
    public void Every_status_has_a_filter_of_the_same_name_that_maps_to_it()
    {
        foreach (var status in Enum.GetValues<RequestStatus>())
        {
            Enum.TryParse<RequestStatusFilter>(status.ToString(), out var filter).ShouldBeTrue($"{status} has no filter value");
            filter.Statuses().ShouldBe([status]);
        }
    }

    [Fact]
    public void Open_and_closed_partition_all_statuses()
    {
        var open = RequestStatusFilter.Open.Statuses();
        var closed = RequestStatusFilter.Closed.Statuses();

        open.Concat(closed).Order().ShouldBe(Enum.GetValues<RequestStatus>().Order());
        closed.ShouldBe([RequestStatus.Completed, RequestStatus.Rejected, RequestStatus.Cancelled], ignoreOrder: true);
    }
}
