using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>
/// Inside an HTTP request: that request's scoped <see cref="ICallerContext"/>. Otherwise (a message handler, a background
/// job): a context built over the ambient state. <see cref="CurrentUser"/> reads the current HTTP request, which does not
/// exist then, and <see cref="IncomingMessage"/> is held in an <see cref="AsyncLocal{T}"/>, so fresh instances of both see
/// the message being handled.
/// </summary>
public sealed class AmbientCallerContextAccessor(
    IHttpContextAccessor http, IOptions<AuthOptions> auth, ServiceIdentity service) : ICallerContextAccessor
{
    public ICallerContext Current =>
        http.HttpContext?.RequestServices.GetService<ICallerContext>()
        ?? new CallerContext(new CurrentUser(http, auth), new IncomingMessage(), service);
}
