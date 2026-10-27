using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using ProcureAi.BuildingBlocks.Web.Options;

namespace ProcureAi.BuildingBlocks.Web.Http;

/// <summary>
/// The settings of a client of someone else's system (the supplier API, the ERP): where it is, the key we present to it, and the
/// resilience pipeline of every call (attempt timeout, retries with exponential backoff and jitter, circuit breaker, total timeout).
/// Each client derives a sealed class of its own with its section name (<c>SupplierClient</c>, <c>ErpClient</c>); the values live in
/// appsettings, the key comes from a secret.
/// </summary>
public abstract class ExternalSystemClientOptions
{
    [Required]
    public Uri? BaseUrl { get; set; }

    /// <summary>Sent in <c>X-Api-Key</c>. From user secrets locally and a Kubernetes secret when deployed; never committed.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = "";

    /// <summary>One attempt gives up after this long.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Retries of a failed attempt (exponential backoff with jitter), on top of the first attempt.</summary>
    [Range(1, 5)]
    public int RetryCount { get; set; } = 2;

    /// <summary>The base delay of the exponential backoff between retries.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:30")]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The whole call, retries included, gives up after this long.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(20);

    [Required, ValidateObjectMembers]
    public ExternalCircuitBreakerOptions CircuitBreaker { get; set; } = new();
}

/// <summary>When the circuit to an external system opens: a share of failed calls among enough calls in the sampling window.</summary>
public sealed class ExternalCircuitBreakerOptions
{
    [Range(0.01, 1.0)]
    public double FailureRatio { get; set; } = 0.5;

    [Range(2, 1000)]
    public int MinimumThroughput { get; set; } = 4;

    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00")]
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the circuit stays open before one trial call is let through.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00")]
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(20);
}

public static class ExternalSystemClient
{
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>The longest refusal message kept (<see cref="RefusalMessageAsync"/>).</summary>
    public const int MaxRefusalLength = 300;

    /// <summary>
    /// Registers a typed client of an external system with its options (<typeparamref name="TOptions"/> from
    /// <paramref name="sectionName"/>, validated at startup): base address and <c>X-Api-Key</c> from the options, and one resilience
    /// pipeline named <paramref name="clientName"/> replacing the service defaults' standard one. From outside in: total timeout,
    /// retries with exponential backoff and jitter, circuit breaker, attempt timeout. The circuit is shared by every call of the process,
    /// so an outage opens it for all callers. The client and its pipeline share the name (metrics tag <c>pipeline.name</c>).
    /// </summary>
    public static IHttpClientBuilder AddExternalSystemClient<TClient, TImplementation, TOptions>(
        this IServiceCollection services, IConfiguration configuration, string sectionName, string clientName)
        where TClient : class
        where TImplementation : class, TClient
        where TOptions : ExternalSystemClientOptions
    {
        services.AddValidatedOptions<TOptions>(configuration, sectionName);
        var builder = services.AddHttpClient<TClient, TImplementation>(clientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<TOptions>>().Value;
            client.BaseAddress = options.BaseUrl;
            client.DefaultRequestHeaders.Add(ApiKeyHeader, options.ApiKey);
        });
        // RemoveAllResilienceHandlers is marked experimental by Microsoft; it is the supported way to replace the defaults' pipeline.
#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.AddResilienceHandler(clientName, (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<TOptions>>().Value;
            var breaker = options.CircuitBreaker;
            pipeline
                .AddTimeout(options.TotalTimeout)
                .AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.RetryCount,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = options.RetryDelay,
                })
                .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = breaker.FailureRatio,
                    MinimumThroughput = breaker.MinimumThroughput,
                    SamplingDuration = breaker.SamplingDuration,
                    BreakDuration = breaker.BreakDuration,
                })
                .AddTimeout(options.AttemptTimeout);
        });
        return builder;
    }

    /// <summary>
    /// Sends a request through an external system's client. A call that never got an answer comes back as
    /// <paramref name="unavailable"/>(message, reason, inner) for the caller's own "try again later" exception, with the reason
    /// <c>circuit_open</c>, <c>timeout</c> or <c>unreachable</c>; the caller's own cancellation is not translated. An answer, whatever
    /// its status, is returned for the caller to judge.
    /// </summary>
    /// <param name="what">What the call is for, in the message (e.g. <c>order PO-1001</c>).</param>
    /// <param name="system">Who is called, in the message (e.g. <c>supplier</c>, <c>ERP</c>).</param>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpRequestMessage request, string system, string what,
        Func<string, string, Exception, Exception> unavailable, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (BrokenCircuitException exception)
        {
            throw unavailable($"{Capitalized(system)} unavailable (circuit open) for {what}.", "circuit_open", exception);
        }
        catch (TimeoutRejectedException exception)
        {
            throw unavailable($"The {system} did not answer in time for {what}.", "timeout", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw unavailable($"The {system} did not answer in time for {what}.", "timeout", exception);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null)
        {
            throw unavailable($"The {system} could not be reached for {what}.", "unreachable", exception);
        }
    }

    /// <summary>
    /// The refusal of an external system in its own words: the problem's validation errors or its detail, else the status. Starts with
    /// <paramref name="refused"/> (e.g. <c>The supplier refused it</c>) and is kept to <see cref="MaxRefusalLength"/> characters,
    /// because it is stored on the entity it refused.
    /// </summary>
    public static async Task<string> RefusalMessageAsync(HttpResponseMessage response, string refused, CancellationToken cancellationToken)
    {
        var fallback = string.Create(CultureInfo.InvariantCulture, $"{refused} (HTTP {(int)response.StatusCode}).");
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            string? message = null;
            if (problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                message = string.Join(" ", errors.EnumerateObject()
                    .SelectMany(e => e.Value.ValueKind == JsonValueKind.Array ? e.Value.EnumerateArray().Select(v => v.ToString()) : [e.Value.ToString()]));
            }

            if (string.IsNullOrWhiteSpace(message) && problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("detail", out var detail))
            {
                message = detail.ToString();
            }

            message = string.IsNullOrWhiteSpace(message) ? fallback : $"{refused}: {message.Trim()}";
            return message.Length <= MaxRefusalLength ? message : message[..MaxRefusalLength];
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>Rand to cents, midpoints away from zero (what the external systems' APIs take).</summary>
    public static long ToCents(decimal rand) => (long)Math.Round(rand * 100m, MidpointRounding.AwayFromZero);

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
