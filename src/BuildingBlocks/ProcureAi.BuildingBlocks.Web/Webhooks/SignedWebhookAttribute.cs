using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.BuildingBlocks.Web.Webhooks;

/// <summary>
/// Marks a controller action as a signed webhook endpoint for <see cref="Source"/>. The action is anonymous (no token): the
/// HMAC signature of the raw body is the authentication. The secret is <c>Webhooks:Sources:&lt;source&gt;:Secret</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SignedWebhookAttribute(string source) : Attribute, IFilterFactory, IAllowAnonymous
{
    public string Source { get; } = source;

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        ActivatorUtilities.CreateInstance<SignedWebhookFilter>(serviceProvider, Source);
}
