using ProcureAi.Assistant.Application.Tools;

namespace ProcureAi.Assistant.Infrastructure.Tools;

/// <summary><see cref="IRequestsLookup"/> over the Requests API, as the user (<c>AddProcureAiUserClient</c>).</summary>
public sealed class RequestsLookup(HttpClient http) : IRequestsLookup
{
    public const string ServiceName = "requests-api";

    public async Task<RequestStatusInfo> GetByNumberAsync(string number, CancellationToken cancellationToken)
    {
        var request = await ToolHttp.GetAsync<RequestAnswer>(
            http, $"api/v1/requests/by-number/{ToolHttp.Escape(number)}", "Requests", cancellationToken);
        return new RequestStatusInfo(request.Id, request.Number, request.Title, request.Status, request.CostCenterCode, request.TotalInclVat);
    }

    /// <summary>The part of Requests' <c>RequestDto</c> the tool shows.</summary>
    private sealed record RequestAnswer(Guid Id, string Number, string Title, string Status, string CostCenterCode, decimal TotalInclVat);
}
