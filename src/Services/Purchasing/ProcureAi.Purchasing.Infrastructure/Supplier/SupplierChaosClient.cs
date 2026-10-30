using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProcureAi.BuildingBlocks.Application.Status;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.Purchasing.Application.Abstractions;

namespace ProcureAi.Purchasing.Infrastructure.Supplier;

/// <summary>
/// The <c>SupplierChaosClient</c> section: retries and timeouts of the chaos switch calls. Short, because the status snapshot reads the
/// switch every few seconds and must not wait long for a simulator that is down (its part is cut off after <c>Status:PartTimeout</c>).
/// </summary>
public sealed class SupplierChaosClientOptions : ServiceClientResilienceOptions
{
    public const string SectionName = "SupplierChaosClient";
}

/// <summary>
/// The simulator's chaos switch through its own endpoint (<c>api/v1/simulators/chaos</c>), with the supplier client's base address and
/// key (<c>SupplierClient</c>). Not through the supplier's resilience pipeline: switching chaos on must not count against the supplier's
/// circuit breaker, and the switch is not slowed by the chaos it sets. Its own retries and timeouts come from
/// <see cref="SupplierChaosClientOptions"/>. The simulator takes the whole configuration (supplier and ERP), so a
/// change reads it first and changes only the supplier's mode; everything else (the ERP, latencies, failure rate) goes back as it was.
/// </summary>
public sealed class SupplierChaosClient(HttpClient http) : ISupplierChaos
{
    /// <summary>The name of the typed client (and of its primary handler in tests).</summary>
    public const string ClientName = "supplier-chaos";

    public const string ChaosPath = "api/v1/simulators/chaos";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ChaosDto> GetAsync(CancellationToken cancellationToken) => Read(await LoadAsync(cancellationToken));

    public async Task<ChaosDto> SetAsync(ChaosMode mode, CancellationToken cancellationToken)
    {
        var configuration = await LoadAsync(cancellationToken);
        var supplier = configuration["supplier"] as JsonObject ?? throw BadAnswer();
        supplier["mode"] = mode.ToString();
        // Set by the simulator only.
        supplier.Remove("restoresAt");
        (configuration["erp"] as JsonObject)?.Remove("restoresAt");

        using var response = await http.PutAsJsonAsync(ChaosPath, configuration, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Read(await response.Content.ReadFromJsonAsync<JsonObject>(Json, cancellationToken) ?? throw BadAnswer());
    }

    private async Task<JsonObject> LoadAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(ChaosPath, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonObject>(Json, cancellationToken) ?? throw BadAnswer();
    }

    private static ChaosDto Read(JsonObject configuration)
    {
        var supplier = configuration["supplier"] as JsonObject ?? throw BadAnswer();
        var mode = Enum.TryParse<ChaosMode>(supplier["mode"]?.GetValue<string>(), ignoreCase: true, out var parsed) ? parsed : throw BadAnswer();
        var restoresAt = supplier["restoresAt"] is JsonValue at && at.TryGetValue<DateTimeOffset>(out var value) ? value : (DateTimeOffset?)null;
        return new ChaosDto(mode, mode == ChaosMode.Normal ? null : restoresAt);
    }

    private static InvalidOperationException BadAnswer() => new("The simulator's chaos switch gave an answer that could not be read.");
}

/// <summary>Purchasing's part of the status snapshot: the supplier's chaos switch (<see cref="SupplierChaosSection"/>).</summary>
public sealed class SupplierChaosStatusContributor(ISupplierChaos chaos) : IStatusContributor
{
    public string Name => SupplierChaosSection.Name;

    public async Task<StatusSection> CollectAsync(CancellationToken ct) => SupplierChaosSection.From(await chaos.GetAsync(ct));
}
