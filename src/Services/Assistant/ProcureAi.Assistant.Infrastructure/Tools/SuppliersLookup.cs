using System.Globalization;
using ProcureAi.Assistant.Application.Tools;

namespace ProcureAi.Assistant.Infrastructure.Tools;

/// <summary><see cref="ISuppliersLookup"/> over the Suppliers API, as the user (<c>AddProcureAiUserClient</c>).</summary>
public sealed class SuppliersLookup(HttpClient http) : ISuppliersLookup
{
    public const string ServiceName = "suppliers-api";

    public async Task<IReadOnlyList<SupplierInfo>> SearchAsync(string search, int pageSize, CancellationToken cancellationToken)
    {
        var page = await ToolHttp.GetAsync<PageAnswer>(
            http,
            $"api/v1/suppliers?search={ToolHttp.Escape(search)}&page=1&pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}",
            "Suppliers",
            cancellationToken);
        return [.. (page.Items ?? []).Select(s => new SupplierInfo(s.Code, s.Name, s.Active, s.ItemCount))];
    }

    /// <summary>Suppliers' <c>PagedResult&lt;SupplierSummaryDto&gt;</c>, the items only.</summary>
    private sealed record PageAnswer(IReadOnlyList<SupplierAnswer>? Items);

    private sealed record SupplierAnswer(string Code, string Name, bool Active, int ItemCount);
}
