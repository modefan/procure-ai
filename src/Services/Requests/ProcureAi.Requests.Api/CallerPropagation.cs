using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Correlation;
using Serilog.Context;
using Wolverine;

namespace ProcureAi.Requests.Api;

/// <summary>The header names that carry the caller across messages. IDs only, never tokens.</summary>
public static class CallerHeaders
{
    public const string CorrelationId = "x-correlation-id";
    public const string TenantId = "x-tenant-id";
    public const string UserId = "x-user-id";

    internal static readonly string[] All = [UserId, TenantId, CorrelationId];
}

public static class CallerPropagationExtensions
{
    /// <summary>
    /// Outgoing messages carry the correlation, tenant and user IDs as envelope headers; handlers that consume a
    /// message get them back in the log scope and as <see cref="Application.Abstractions.ICallerContext"/>.
    /// </summary>
    public static void AddCallerPropagation(this WolverineOptions options)
    {
        // HttpContextAccessor keeps the current request in one static AsyncLocal, so a new instance sees the same request.
        options.MetadataRules.Add(new CallerHeadersRule(new HttpContextAccessor()));
        options.Policies.AddMiddleware(typeof(RestoreCallerMiddleware));
    }
}

/// <summary>Stamps the caller of the current HTTP request (or of the message being handled) on every outgoing envelope.</summary>
internal sealed class CallerHeadersRule(IHttpContextAccessor accessor) : IEnvelopeRule
{
    public void Modify(Envelope envelope) => StampFromHttp(envelope);

    public void ApplyCorrelation(IMessageContext originator, Envelope outgoing)
    {
        // A message sent while handling another one passes the caller on; inside an HTTP request the request's caller wins.
        if (originator.Envelope is { } incoming)
        {
            foreach (var name in CallerHeaders.All)
            {
                if (incoming.Headers.TryGetValue(name, out var value))
                {
                    Set(outgoing, name, value);
                }
            }
        }

        StampFromHttp(outgoing);
    }

    private void StampFromHttp(Envelope envelope)
    {
        if (accessor.HttpContext is not { } http)
        {
            return;
        }

        var user = new CurrentUser(accessor);
        Set(envelope, CallerHeaders.UserId, user.UserId);
        Set(envelope, CallerHeaders.TenantId, user.TenantId);
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

/// <summary>The envelope of the message being handled, for the scoped <see cref="ApiCallerContext"/>.</summary>
public sealed class IncomingMessage
{
    public Envelope? Envelope { get; set; }
}

/// <summary>Wolverine middleware: makes the incoming message's caller headers available to the handler and its logs.</summary>
public static class RestoreCallerMiddleware
{
    public static IDisposable Before(Envelope envelope, IncomingMessage incoming)
    {
        incoming.Envelope = envelope;
        var scopes = new List<IDisposable>();
        Push(scopes, envelope, CallerHeaders.CorrelationId, CorrelationIdMiddleware.LogProperty);
        Push(scopes, envelope, CallerHeaders.TenantId, "TenantId");
        Push(scopes, envelope, CallerHeaders.UserId, "UserId");
        return new Scopes(scopes);
    }

    public static void Finally(IDisposable scopes) => scopes.Dispose();

    private static void Push(List<IDisposable> scopes, Envelope envelope, string header, string property)
    {
        if (envelope.Headers.TryGetValue(header, out var value) && !string.IsNullOrEmpty(value))
        {
            scopes.Add(LogContext.PushProperty(property, value));
        }
    }

    private sealed class Scopes(List<IDisposable> scopes) : IDisposable
    {
        public void Dispose()
        {
            for (var i = scopes.Count - 1; i >= 0; i--)
            {
                scopes[i].Dispose();
            }
        }
    }
}
