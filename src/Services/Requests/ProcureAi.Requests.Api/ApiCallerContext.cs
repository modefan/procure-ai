using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.Requests.Application.Abstractions;

namespace ProcureAi.Requests.Api;

/// <summary>
/// The caller of a handler: the authenticated HTTP user, or, for a message consumed from the broker (no HTTP
/// request), the user and tenant that travelled in the message headers.
/// </summary>
public sealed class ApiCallerContext(ICurrentUser user, IncomingMessage incoming) : ICallerContext
{
    private bool FromHttp => user.UserId != CurrentUser.AnonymousUserId;

    public string UserId => FromHttp ? user.UserId : Header(CallerHeaders.UserId) ?? user.UserId;

    public string? TenantId => FromHttp ? user.TenantId : Header(CallerHeaders.TenantId);

    private string? Header(string name) =>
        incoming.Envelope is { } envelope && envelope.Headers.TryGetValue(name, out var value) ? value : null;
}
