using System.Net.Http.Headers;
using System.Net.Http.Json;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using Shouldly;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// Emails (plan 04 ruling 9): MailKit over SMTP to Mailpit, the address from Access (<c>internal/users/{userId}</c>), one email per message
/// and recipient however often RabbitMQ delivers the message (dedup key <c>email:{messageId}:{userId}</c>). Each test uses a tenant and
/// addresses of its own.
/// </summary>
[Collection(NotificationsCollection.Name)]
public sealed class EmailTests(NotificationsApp app)
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewTenant() => $"t-{Guid.NewGuid():N}"[..16];

    private async Task<IReadOnlyList<MailpitMessage>> WaitForEmailsAsync(string address, int count)
    {
        var deadline = DateTime.UtcNow + Arrival;
        while (true)
        {
            var messages = await app.Mailpit.MessagesToAsync(address, Ct);
            if (messages.Count >= count)
            {
                return messages;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{messages.Count} of {count} email(s) to {address} arrived.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }

    private async Task<int> BellCountAsync(string tenant, string user)
    {
        using var client = app.A.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.CreateToken(user, tenant, "manager"));
        using var response = await client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
        return body.GetProperty("items").GetArrayLength();
    }

    [Fact]
    public async Task ApprovalRequested_adds_bell_item_for_approver_and_sends_one_email()
    {
        var tenant = NewTenant();
        var requestId = Guid.NewGuid();
        var maria = $"maria-{tenant}@acme.test";
        app.Access.SetDisplayName(tenant, "maria", "Maria Manager", maria);
        var captured = await app.Probe.BindQueueAsync(Exchanges.Approvals);

        await app.PublishAsync(new ApprovalRequested(
            requestId, tenant, "PR-1043", Guid.NewGuid(), 1, "Manager", ["maria"], DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow));

        var email = (await WaitForEmailsAsync(maria, 1)).Single();
        email.Subject.ShouldBe("PR-1043 is waiting for your approval");
        email.FromAddress.ShouldBe("no-reply@procure-ai.test");
        (await app.Mailpit.TextAsync(email.Id, Ct)).ShouldContain($"{NotificationsApiFactory.AppBaseUrl}/requests/{requestId}");
        (await BellCountAsync(tenant, "maria")).ShouldBe(1);

        // RabbitMQ delivers the same message again (same message ID): no second email, no second bell item.
        var raw = await app.Probe.WaitForRawAsync(captured, Arrival);
        raw.MessageId.ShouldNotBeNull();
        await app.Probe.RepublishAsync(Exchanges.Approvals, raw);
        await app.Probe.RepublishAsync(Exchanges.Approvals, raw);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        (await app.Mailpit.MessagesToAsync(maria, Ct)).Count.ShouldBe(1);
        (await BellCountAsync(tenant, "maria")).ShouldBe(1);
    }

    [Fact]
    public async Task Each_approver_due_gets_an_email_and_a_user_without_an_address_only_the_bell_item()
    {
        var tenant = NewTenant();
        var maria = $"maria-{tenant}@acme.test";
        var fin = $"fin-{tenant}@acme.test";
        app.Access.SetDisplayName(tenant, "maria", "Maria Manager", maria);
        app.Access.SetDisplayName(tenant, "fin", "Fiona Finance", fin);
        app.Access.SetDisplayName(tenant, "nomail", "No Mail");

        await app.PublishAsync(new ApprovalRequested(
            Guid.NewGuid(), tenant, "PR-2001", Guid.NewGuid(), 1, "Finance", ["maria", "fin", "nomail"], DateTimeOffset.UtcNow.AddDays(2),
            DateTimeOffset.UtcNow));

        await WaitForEmailsAsync(maria, 1);
        await WaitForEmailsAsync(fin, 1);
        (await BellCountAsync(tenant, "nomail")).ShouldBe(1);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        (await app.Mailpit.MessagesAsync(Ct)).Count(m => m.To.Any(t => t.Contains(tenant, StringComparison.Ordinal))).ShouldBe(2, "one each for Maria and Fiona");
    }
}
