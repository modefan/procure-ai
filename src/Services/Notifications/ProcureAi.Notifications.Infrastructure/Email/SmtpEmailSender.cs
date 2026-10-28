using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using ProcureAi.Notifications.Application.Email;
using StackExchange.Redis;

namespace ProcureAi.Notifications.Infrastructure.Email;

/// <summary>
/// Sends notification emails with MailKit over SMTP (<c>Notifications:Smtp</c>; Mailpit locally): plain text plus simple HTML. One
/// connection per email: a few emails a minute at most, and nothing to keep alive. STARTTLS when the server offers it.
/// </summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var smtp = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(smtp.FromName, smtp.From));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToAddress));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(smtp.Host, smtp.Port, SecureSocketOptions.Auto, cancellationToken);
        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}

/// <summary>The dedup keys of <see cref="IEmailOnce"/> in Redis: <c>SET key sending NX EX</c>, then <c>SET key sent EX window</c>.</summary>
public sealed class RedisEmailOnce(IConnectionMultiplexer redis) : IEmailOnce
{
    public Task<bool> TryClaimAsync(string key, TimeSpan ttl, CancellationToken cancellationToken) =>
        redis.GetDatabase().StringSetAsync(key, "sending", ttl, When.NotExists);

    // Unconditional: also when a very slow send outlived the claim.
    public Task ConfirmAsync(string key, TimeSpan window, CancellationToken cancellationToken) =>
        redis.GetDatabase().StringSetAsync(key, "sent", window);

    public Task ReleaseAsync(string key, CancellationToken cancellationToken) =>
        redis.GetDatabase().KeyDeleteAsync(key);
}
