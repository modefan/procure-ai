namespace ProcureAi.Assistant.Application.Tools;

/// <summary>A purchase request as <c>get_request_status</c> shows it. <see cref="Status"/> is Requests' status name (<c>Submitted</c>).</summary>
public sealed record RequestStatusInfo(Guid Id, string Number, string Title, string Status, string CostCenterCode, decimal TotalInclVat);

/// <summary>The Requests service, called as the signed-in user (its permissions and tenant).</summary>
public interface IRequestsLookup
{
    /// <summary><c>GET api/v1/requests/by-number/{number}</c>; throws <see cref="ToolLookupException"/> (404 included).</summary>
    Task<RequestStatusInfo> GetByNumberAsync(string number, CancellationToken cancellationToken);
}
