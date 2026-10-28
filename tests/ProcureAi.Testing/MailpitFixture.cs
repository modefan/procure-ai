using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ProcureAi.Testing;

/// <summary>One email Mailpit received, as its HTTP API lists it.</summary>
public sealed record MailpitMessage(string Id, string FromAddress, IReadOnlyList<string> To, string Subject);

/// <summary>
/// Mailpit, the AppHost's mail catcher (same image), with the caps of the <c>Mailpit</c> section of tests/testcontainers.settings.json.
/// SMTP on <see cref="SmtpPort"/>; what arrived is read through its HTTP API (<c>GET /api/v1/messages</c>).
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    private const string Image = "axllent/mailpit:v1.32.0";
    private const int Smtp = 1025;
    private const int Http = 8025;

    private IContainer? _container;
    private HttpClient? _http;

    public string SmtpHost => _container!.Hostname;

    public int SmtpPort => _container!.GetMappedPublicPort(Smtp);

    public async ValueTask InitializeAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(Smtp, true)
            .WithPortBinding(Http, true)
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "Mailpit"))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/livez").ForPort(Http)))
            .Build();
        await _container.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(Http)}") };
    }

    /// <summary>Every message received so far (newest first), with the addresses it was sent to.</summary>
    public async Task<IReadOnlyList<MailpitMessage>> MessagesAsync(CancellationToken cancellationToken = default)
    {
        var page = await _http!.GetFromJsonAsync<MessagesPage>("/api/v1/messages?limit=1000", cancellationToken)
            ?? throw new InvalidOperationException("Mailpit returned no message list.");
        return [.. page.Messages.Select(m => new MailpitMessage(m.Id, m.From?.Address ?? "", [.. m.To.Select(t => t.Address)], m.Subject))];
    }

    /// <summary>The messages sent to <paramref name="address"/>.</summary>
    public async Task<IReadOnlyList<MailpitMessage>> MessagesToAsync(string address, CancellationToken cancellationToken = default) =>
        [.. (await MessagesAsync(cancellationToken)).Where(m => m.To.Contains(address, StringComparer.OrdinalIgnoreCase))];

    /// <summary>The plain-text body of one message.</summary>
    public async Task<string> TextAsync(string id, CancellationToken cancellationToken = default)
    {
        var message = await _http!.GetFromJsonAsync<MessageDetail>($"/api/v1/message/{id}", cancellationToken);
        return message?.Text ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private sealed record MessagesPage([property: JsonPropertyName("messages")] IReadOnlyList<MessageSummary> Messages);

    private sealed record MessageSummary(
        [property: JsonPropertyName("ID")] string Id,
        [property: JsonPropertyName("From")] MailAddress? From,
        [property: JsonPropertyName("To")] IReadOnlyList<MailAddress> To,
        [property: JsonPropertyName("Subject")] string Subject);

    private sealed record MailAddress([property: JsonPropertyName("Address")] string Address);

    private sealed record MessageDetail([property: JsonPropertyName("Text")] string Text);
}
