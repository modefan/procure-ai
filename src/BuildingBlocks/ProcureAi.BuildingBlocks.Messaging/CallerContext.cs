using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Auth;
using Wolverine;

namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>The name of the service this process runs as (from its composition root), e.g. <c>requests-api</c>.</summary>
public sealed record ServiceIdentity(string Name);

/// <summary>
/// The envelope of the message being handled, filled by <see cref="RestoreCallerMiddleware"/>.
/// <para>
/// Held in an <see cref="AsyncLocal{T}"/>, not in the instance: Wolverine's generated handler code builds some scoped
/// objects itself while others (the DbContext, which the audit interceptor asks for the caller) come from the
/// container, so two <see cref="IncomingMessage"/> instances can exist for one message. They all see the message of
/// the current handler's execution flow, and concurrent handlers never see each other's.
/// </para>
/// </summary>
public sealed class IncomingMessage
{
    private static readonly AsyncLocal<Envelope?> Current = new();

    public Envelope? Envelope
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}

/// <summary>
/// The caller of a handler: the authenticated HTTP caller, or, for a message consumed from the broker (no HTTP
/// request), the user and tenant that travelled in the message headers; with neither, the system.
/// <para>
/// Message headers are trusted as they are: only our own services publish to the broker, which is internal and not
/// reachable from outside the cluster. If the broker were ever exposed, these headers could be forged.
/// </para>
/// Registered scoped as a class (not a lambda) because Wolverine's generated handler code constructs it.
/// </summary>
public sealed class CallerContext(ICurrentUser user, IncomingMessage incoming, ServiceIdentity service) : ICallerContext
{
    public string UserId => Kind switch
    {
        CallerKind.User => user.UserId,
        CallerKind.AdminApiKey => user.OnBehalfOfUser ?? user.UserId,
        CallerKind.Service => user.OnBehalfOfUser ?? SystemCaller.Id,
        CallerKind.Message => Header(CallerHeaders.UserId) ?? SystemCaller.Id,
        _ => SystemCaller.Id,
    };

    public string ActorId => Kind switch
    {
        CallerKind.User or CallerKind.AdminApiKey => user.UserId,
        CallerKind.Service => SystemCaller.Service(user.CallingService ?? user.UserId),
        _ => SystemCaller.Service(service.Name),
    };

    public string? TenantId => Kind switch
    {
        CallerKind.Message => Header(CallerHeaders.TenantId),
        CallerKind.System => null,
        _ => user.TenantId,
    };

    public string? CallingService => Kind switch
    {
        CallerKind.AdminApiKey or CallerKind.Service => user.CallingService,
        CallerKind.Message => Header(CallerHeaders.CallingService),
        _ => null,
    };

    public CallerKind Kind =>
        user.IsAuthenticated
            ? user.IsAdminApiKey ? CallerKind.AdminApiKey : user.IsServiceAccount ? CallerKind.Service : CallerKind.User
            : incoming.Envelope is not null ? CallerKind.Message : CallerKind.System;

    private string? Header(string name) =>
        incoming.Envelope is { } envelope && envelope.Headers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;
}
