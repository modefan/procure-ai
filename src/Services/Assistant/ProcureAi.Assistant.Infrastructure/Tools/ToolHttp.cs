using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcureAi.Assistant.Application.Tools;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.BuildingBlocks.Web.Http;

namespace ProcureAi.Assistant.Infrastructure.Tools;

/// <summary>The <c>Assistant:ToolClients</c> section: retries and timeouts of the tools' calls to other services (each sits in a user's answer).</summary>
public sealed class ToolClientsOptions : ServiceClientResilienceOptions
{
    public const string SectionName = "Assistant:ToolClients";
}

/// <summary>
/// One GET of a tool lookup as the user: the answer as <c>T</c>, or a <see cref="ToolLookupException"/> saying why not (401, 403, 404;
/// any other non-success, a failed or refused call, a client timeout or an unreadable body is "unavailable"). A cancelled question and a
/// missing credential (<c>UserCredentialMissingException</c>) pass through, and so does a bug such as the relay's refusal of a foreign host.
/// </summary>
internal static class ToolHttp
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static async Task<T> GetAsync<T>(HttpClient http, string url, string service, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ToolLookupException(
                    response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => ToolFailure.Unauthorized,
                        HttpStatusCode.Forbidden => ToolFailure.Forbidden,
                        HttpStatusCode.NotFound => ToolFailure.NotFound,
                        _ => ToolFailure.Unavailable,
                    },
                    $"{service} answered {(int)response.StatusCode}.");
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw new ToolLookupException(ToolFailure.Unavailable, $"{service} returned an empty body.");
        }
        catch (Exception exception) when (exception is not ToolLookupException && !cancellationToken.IsCancellationRequested
            && (DependencyFailures.IsTransient(exception) || exception is HttpRequestException or TaskCanceledException or JsonException))
        {
            throw new ToolLookupException(ToolFailure.Unavailable, $"{service} could not be reached.", exception);
        }
    }

    /// <summary>A path or query string value.</summary>
    public static string Escape(string value) => Uri.EscapeDataString(value);
}
