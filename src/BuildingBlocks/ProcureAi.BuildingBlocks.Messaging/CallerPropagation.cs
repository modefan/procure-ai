using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Correlation;
using Serilog.Context;
using Wolverine;
using Wolverine.Runtime;

namespace ProcureAi.BuildingBlocks.Messaging;

public static class CallerPropagationExtensions
{
    /// <summary>
    /// Registers what the caller model needs: the service's identity and the scoped <see cref="ICallerContext"/>
    /// (<see cref="CallerContext"/>). Call it from the composition root with the service name (e.g. <c>requests-api</c>).
    /// </summary>
    public static IServiceCollection AddProcureAiCaller(this IServiceCollection services, string serviceName)
    {
        services.AddHttpContextAccessor();
        services.TryAddScoped<ICurrentUser, CurrentUser>();
        services.TryAddScoped<IncomingMessage>();
        services.AddSingleton(new ServiceIdentity(serviceName));
        services.AddScoped<ICallerContext, CallerContext>();
        return services;
    }

    /// <summary>
    /// Outgoing messages carry the correlation, tenant and user IDs as envelope headers; handlers that consume a
    /// message get them back in the log scope and as <see cref="ICallerContext"/>.
    /// </summary>
    public static void AddCallerPropagation(this WolverineOptions options)
    {
        options.Services.TryAddScoped<IncomingMessage>();
        // HttpContextAccessor keeps the current request in one static AsyncLocal, so a new instance sees the same request.
        options.MetadataRules.Add(new CallerHeadersRule(new HttpContextAccessor()));
        options.Policies.AddMiddleware(typeof(RestoreCallerMiddleware));
    }
}

/// <summary>
/// Stamps the caller of the current HTTP request (or of the message being handled) on every outgoing envelope.
/// <c>x-calling-service</c> is always the sending service's own name, never copied or taken from the HTTP caller,
/// so a message cannot claim to come from a service that did not send it.
/// </summary>
internal sealed class CallerHeadersRule(IHttpContextAccessor accessor) : IEnvelopeRule
{
    public void Modify(Envelope envelope) => StampFromHttp(envelope);

    public void ApplyCorrelation(IMessageContext originator, Envelope outgoing)
    {
        // A message sent while handling another one passes the initiating user on (never the acting service);
        // inside an HTTP request the request's caller wins.
        if (originator.Envelope is { } incoming)
        {
            foreach (var name in CallerHeaders.All)
            {
                if (name != CallerHeaders.CallingService && incoming.Headers.TryGetValue(name, out var value))
                {
                    Set(outgoing, name, value);
                }
            }
        }

        StampFromHttp(outgoing);
        var sender = (originator as MessageContext)?.Runtime.Services.GetService<ServiceIdentity>();
        Set(outgoing, CallerHeaders.CallingService, sender?.Name);
    }

    private void StampFromHttp(Envelope envelope)
    {
        if (accessor.HttpContext is not { } http)
        {
            return;
        }

        // The initiating user (the person on whose behalf the call was made), not the admin key or service account.
        if (http.RequestServices.GetService<ICallerContext>() is { } caller)
        {
            Set(envelope, CallerHeaders.UserId, caller.UserId);
            Set(envelope, CallerHeaders.TenantId, caller.TenantId);
        }

        Set(envelope, CallerHeaders.CallingService, http.RequestServices.GetService<ServiceIdentity>()?.Name);

        Set(envelope, CallerHeaders.CorrelationId, http.Items[CorrelationIdMiddleware.ItemKey] as string);
    }

    private static void Set(Envelope envelope, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            envelope.Headers[name] = value;
        }
    }
}

/// <summary>Wolverine middleware: makes the incoming message's caller headers available to the handler and its logs.</summary>
public static class RestoreCallerMiddleware
{
    public static IDisposable Before(Envelope envelope, IncomingMessage incoming)
    {
        var previous = incoming.Envelope;
        incoming.Envelope = envelope;
        var scopes = new List<IDisposable>();
        Push(scopes, envelope, CallerHeaders.CorrelationId, CorrelationIdMiddleware.LogProperty);
        Push(scopes, envelope, CallerHeaders.TenantId, "TenantId");
        Push(scopes, envelope, CallerHeaders.UserId, "UserId");
        return new Scopes(scopes, () => incoming.Envelope = previous);
    }

    public static void Finally(IDisposable scopes) => scopes.Dispose();

    private static void Push(List<IDisposable> scopes, Envelope envelope, string header, string property)
    {
        if (envelope.Headers.TryGetValue(header, out var value) && !string.IsNullOrEmpty(value))
        {
            scopes.Add(LogContext.PushProperty(property, value));
        }
    }

    private sealed class Scopes(List<IDisposable> scopes, Action restore) : IDisposable
    {
        public void Dispose()
        {
            for (var i = scopes.Count - 1; i >= 0; i--)
            {
                scopes[i].Dispose();
            }

            restore();
        }
    }
}
