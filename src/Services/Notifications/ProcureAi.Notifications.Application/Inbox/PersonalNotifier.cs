using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.Contracts;
using ProcureAi.Notifications.Application.Email;
using ProcureAi.Notifications.Application.Live;

namespace ProcureAi.Notifications.Application.Inbox;

/// <summary>Tells the users an event concerns: a bell item (pushed live) and an email each.</summary>
public interface IPersonalNotifier
{
    /// <summary>
    /// Handles <paramref name="integrationEvent"/> per <see cref="RecipientRules"/>. <paramref name="messageId"/> (the same on every delivery of
    /// the message) makes a redelivery harmless: the bell item has that ID and the email is deduplicated on it.
    /// </summary>
    Task NotifyAsync(IIntegrationEvent integrationEvent, Guid? messageId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class PersonalNotifier(
    IBellStore bells,
    ILivePushes pushes,
    IAccessDirectory access,
    IEmailSender email,
    IEmailOnce once,
    ICacheInvalidator cache,
    IOptions<SmtpOptions> smtp,
    ILogger<PersonalNotifier> logger) : IPersonalNotifier
{
    /// <summary>The cached <c>GET api/v1/notifications</c> answers of a tenant (formatted with <see cref="CacheTag.Format"/>).</summary>
    public const string CacheTagTemplate = "notifications:{tenant}";

    public async Task NotifyAsync(IIntegrationEvent integrationEvent, Guid? messageId, CancellationToken cancellationToken)
    {
        if (RecipientRules.For(integrationEvent) is not { } notice)
        {
            return;
        }

        // Without a message ID (never over RabbitMQ) the item and email get one of their own: nothing to deduplicate against.
        var id = (messageId ?? Guid.NewGuid()).ToString("N");
        var item = new BellItem(id, notice.Text, notice.RequestId, RecipientRules.Link(notice.RequestId), integrationEvent.OccurredAt, Read: false);
        foreach (var userId in notice.Recipients)
        {
            // Pushed whenever the user has the item after the add, also on a redelivery or retry (the first push may be what failed);
            // the client keys items by ID, so a repeated push changes nothing. An item already trimmed away is not pushed.
            if (await bells.AddAsync(notice.TenantId, userId, item, cancellationToken))
            {
                await pushes.PushToUserAsync(notice.TenantId, userId, item, cancellationToken);
            }
        }

        await EvictAsync(notice.TenantId);

        var failures = new List<Exception>();
        foreach (var userId in notice.Recipients)
        {
            try
            {
                await EmailOnceAsync(notice, userId, id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            // Every recipient was tried; the retry sends only the emails that were not sent.
            throw new EmailDeliveryException(
                $"{failures.Count} of {notice.Recipients.Count} notification email(s) could not be sent.", new AggregateException(failures));
        }
    }

    private async Task EmailOnceAsync(Notice notice, string userId, string messageId, CancellationToken cancellationToken)
    {
        var key = $"email:{messageId}:{userId}";
        // A short claim while sending: if this instance dies before sending, the redelivered message finds it expired and sends.
        if (!await once.TryClaimAsync(key, smtp.Value.SendingClaimTtl, cancellationToken))
        {
            return;
        }

        try
        {
            var user = await access.GetUserAsync(userId, notice.TenantId, cancellationToken);
            if (user is null || string.IsNullOrWhiteSpace(user.Email))
            {
                // Nobody to write to: kept as sent, a retry would not find an address either.
                logger.LogInformation("No email address for user {UserId} of tenant {TenantId}; bell item only", userId, notice.TenantId);
            }
            else
            {
                await email.SendAsync(Compose(notice, user), cancellationToken);
            }
        }
        catch
        {
            await once.ReleaseAsync(key, CancellationToken.None);
            throw;
        }

        // Sent: no second email for this message and user within the dedup window. A failure here leaves the short claim, which only
        // means a redelivery after it expired could send once more.
        await once.ConfirmAsync(key, smtp.Value.DedupWindow, CancellationToken.None);
    }

    private EmailMessage Compose(Notice notice, DirectoryUser user)
    {
        var link = smtp.Value.AppBaseUrl.TrimEnd('/') + RecipientRules.Link(notice.RequestId);
        var text = $"Hello {user.DisplayName},\n\n{notice.Text}.\n\nOpen the request: {link}\n\nProcure AI";
        var html =
            $"<p>Hello {WebUtility.HtmlEncode(user.DisplayName)},</p>" +
            $"<p>{WebUtility.HtmlEncode(notice.Text)}.</p>" +
            $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Open the request</a></p>" +
            "<p>Procure AI</p>";
        return new EmailMessage(user.Email, user.DisplayName, notice.Text, text, html);
    }

    private async Task EvictAsync(string tenantId)
    {
        try
        {
            if (CacheTag.Format(CacheTagTemplate, CacheTag.Tenant(tenantId)) is { } tag)
            {
                await cache.RemoveByTagAsync([tag], CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            // The cached list expires within a second anyway; the client got the item pushed.
            logger.LogWarning(exception, "Cache invalidation failed for the notifications of {TenantId}", tenantId);
        }
    }
}
