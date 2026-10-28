using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Notifications.Application.Email;

/// <summary>
/// <c>Notifications:Smtp</c>: the SMTP server the notification emails go to (Mailpit locally), the sender, the app's public address for
/// the links in the emails, and how long a sent email is remembered so a redelivered message sends no second one.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Notifications:Smtp";

    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = 25;

    /// <summary>The sender address.</summary>
    [Required(AllowEmptyStrings = false), EmailAddress]
    public string From { get; set; } = "";

    /// <summary>The sender name shown in mail clients.</summary>
    [Required(AllowEmptyStrings = false), MaxLength(100)]
    public string FromName { get; set; } = "Procure AI";

    /// <summary>The app as the user opens it (the gateway), e.g. <c>https://localhost:7443</c>; links are this plus the app route.</summary>
    [Required(AllowEmptyStrings = false), Url]
    public string AppBaseUrl { get; set; } = "";

    /// <summary>How long the dedup key <c>email:{messageId}:{userId}</c> is kept. Default 7 days.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00")]
    public TimeSpan DedupWindow { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>One email: plain text plus simple HTML.</summary>
public sealed record EmailMessage(string ToAddress, string ToName, string Subject, string TextBody, string HtmlBody);

/// <summary>Sends an email (SMTP, in Infrastructure). Throws when the server did not take it.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Remembers which emails were sent (Redis <c>SET NX</c>), so each (message, recipient) gets one email however often the message is
/// delivered. A claim that could not be used (the send failed) is released, so the retry sends.
/// </summary>
public interface IEmailOnce
{
    /// <summary>True when this caller is the first to claim <paramref name="key"/> within <paramref name="window"/>.</summary>
    Task<bool> TryClaimAsync(string key, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>Gives the claim back (the email was not sent).</summary>
    Task ReleaseAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// An email could not be sent (the address lookup or the SMTP server failed). The message is retried (<c>Notifications:PushRetry</c>):
/// the bell item is not added twice and emails already sent are not sent again.
/// </summary>
public sealed class EmailDeliveryException(string message, Exception inner) : Exception(message, inner);
