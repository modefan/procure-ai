using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.BuildingBlocks.Web.Correlation;

/// <summary>
/// Adds <c>X-Correlation-Id</c> (the one <see cref="CorrelationIdMiddleware"/> chose for the current request) to every
/// outgoing HTTP call, so one correlation ID follows a request across services. A call made outside a request, or that
/// already sets the header, is left alone.
/// </summary>
public sealed class CorrelationIdHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(CorrelationIdMiddleware.HeaderName)
            && accessor.HttpContext?.Items[CorrelationIdMiddleware.ItemKey] is string correlationId)
        {
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public static class CorrelationIdHandlerSetup
{
    /// <summary>Puts <see cref="CorrelationIdHandler"/> on every <see cref="HttpClient"/> created through <c>IHttpClientFactory</c>.</summary>
    public static IServiceCollection AddProcureAiCorrelationPropagation(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddTransient<CorrelationIdHandler>();
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<CorrelationIdHandler>());
        return services;
    }
}
