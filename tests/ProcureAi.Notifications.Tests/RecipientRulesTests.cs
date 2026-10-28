using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Notifications;
using ProcureAi.Contracts.Requests;
using ProcureAi.Notifications.Application.Email;
using ProcureAi.Notifications.Application.Inbox;
using ProcureAi.Notifications.Application.Live;
using ProcureAi.Notifications.Application.Presence;
using Shouldly;

namespace ProcureAi.Notifications.Tests;

/// <summary>
/// Who gets a bell item and an email for which event (plan 04 ruling 8), and the notifier's once-per-message guarantees.
/// </summary>
public sealed class RecipientRulesTests
{
    private const string Tenant = "acme";
    private static readonly Guid RequestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StepId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset At = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApprovalRequested Requested(params string[] approvers) =>
        new(RequestId, Tenant, "PR-1043", StepId, 1, "Manager", approvers, At.AddDays(2), At);

    private static readonly string[] PersonalNames =
    [
        "ApprovalRequested", "ApprovalReminderDue", "ApprovalEscalated",
        "RequestApproved", "RequestRejected", "RequestCompleted", "RequestCancellationRefused",
    ];

    public static TheoryData<string> PersonalCases => [.. PersonalNames];

    private static readonly Dictionary<string, (IIntegrationEvent Event, string[] Recipients, string Text)> Cases = new()
    {
        ["ApprovalRequested"] = (Requested("maria"), ["maria"], "PR-1043 is waiting for your approval"),
        ["ApprovalReminderDue"] = (new ApprovalReminderDue(RequestId, Tenant, StepId, ["maria"], 1, At, "PR-1043"), ["maria"],
            "Reminder: PR-1043 is still waiting for your approval"),
        ["ApprovalEscalated"] = (new ApprovalEscalated(RequestId, Tenant, StepId, "Manager", "DepartmentHead", ["dept"], At, "PR-1043"), ["dept"],
            "PR-1043 was escalated to you for approval"),
        ["RequestApproved"] = (new RequestApproved(RequestId, Tenant, "PR-1043", 100m, ["maria"], At, "tom"), ["tom"],
            "Your request PR-1043 was approved"),
        ["RequestRejected"] = (new RequestRejected(RequestId, Tenant, "PR-1043", "Too expensive", "maria", At, "tom"), ["tom"],
            "Your request PR-1043 was rejected: Too expensive"),
        ["RequestCompleted"] = (new RequestCompleted(RequestId, Tenant, "PR-1043", 100m, At, "tom"), ["tom"],
            "Your request PR-1043 is complete: it has been paid in full"),
        ["RequestCancellationRefused"] = (new RequestCancellationRefused(RequestId, Tenant, "PR-1043", "Goods were received", At, "tom"), ["tom"],
            "The cancellation of your request PR-1043 was refused: Goods were received"),
    };

    [Theory]
    [MemberData(nameof(PersonalCases))]
    public void Each_personal_event_tells_its_recipients(string caseName)
    {
        var (integrationEvent, recipients, text) = Cases[caseName];

        var notice = RecipientRules.For(integrationEvent).ShouldNotBeNull();

        notice.TenantId.ShouldBe(Tenant);
        notice.RequestId.ShouldBe(RequestId);
        notice.Recipients.ShouldBe(recipients);
        notice.Text.ShouldBe(text);
    }

    [Fact]
    public void Approvers_due_are_told_once_each()
    {
        RecipientRules.For(Requested("maria", "fin", "maria", "")).ShouldNotBeNull().Recipients.ShouldBe(["maria", "fin"]);
    }

    [Fact]
    public void Events_of_other_kinds_tell_nobody()
    {
        RecipientRules.For(new ApprovalStepApproved(RequestId, Tenant, StepId, "maria", At)).ShouldBeNull();
        RecipientRules.For(new ApprovalAlertRaised(RequestId, Tenant, StepId, "No approver", At)).ShouldBeNull("system status, plan 06");
        RecipientRules.For(new RequestOrdered(RequestId, Tenant, "PR-1043", Guid.NewGuid(), "PO-1", At)).ShouldBeNull();
        RecipientRules.For(new RequestSubmitted(RequestId, Tenant, "PR-1043", "Laptops", "tom", "CC-100", 1m, At, "sup", [])).ShouldBeNull();
    }

    [Fact]
    public void A_message_from_before_the_recipient_was_added_tells_nobody()
    {
        // Published by an Approvals from before plan 04: no RequesterId in the JSON.
        RecipientRules.For(new RequestApproved(RequestId, Tenant, "PR-1043", 100m, ["maria"], At)).ShouldBeNull();
        RecipientRules.For(new ApprovalReminderDue(RequestId, Tenant, StepId, ["maria"], 1, At)).ShouldNotBeNull()
            .Text.ShouldBe("Reminder: A request is still waiting for your approval");
    }

    [Fact]
    public void Every_personal_event_has_a_handler_that_notifies()
    {
        // The handler methods of the personal events take the notifier (a type-specific method would otherwise hide it).
        var handler = typeof(ProcureAi.Notifications.Application.Handlers.ApprovalEventsHandler);
        foreach (var name in PersonalNames)
        {
            var method = handler.GetMethods().Single(m => m.Name == "Handle" && m.GetParameters()[0].ParameterType.Name == name);
            method.GetParameters().ShouldContain(p => p.ParameterType == typeof(IPersonalNotifier), name);
        }
    }

    [Fact]
    public async Task One_message_delivered_twice_gives_one_bell_item_and_one_email_per_recipient()
    {
        var world = new World();
        var messageId = Guid.NewGuid();
        var requested = Requested("maria", "fin");

        await world.Notifier.NotifyAsync(requested, messageId, Ct);
        await world.Notifier.NotifyAsync(requested, messageId, Ct);

        world.Bells.Items.Select(i => (i.User, i.Item.Id)).ShouldBe([("maria", messageId.ToString("N")), ("fin", messageId.ToString("N"))]);
        world.Pushes.ToUsers.Select(p => p.User).ShouldBe(["maria", "fin"]);
        world.Email.Sent.Select(m => m.ToAddress).ShouldBe(["maria@acme.test", "fin@acme.test"]);
        var mail = world.Email.Sent[0];
        mail.Subject.ShouldBe("PR-1043 is waiting for your approval");
        mail.TextBody.ShouldContain($"https://app.test/requests/{RequestId}");
        mail.HtmlBody.ShouldContain($"href=\"https://app.test/requests/{RequestId}\"");
        world.Bells.Items[0].Item.Link.ShouldBe($"/requests/{RequestId}");
    }

    [Fact]
    public async Task A_failed_email_is_sent_on_the_retry_and_the_others_are_not_sent_again()
    {
        var world = new World();
        var messageId = Guid.NewGuid();
        world.Email.FailFor.Add("maria@acme.test");

        await Should.ThrowAsync<EmailDeliveryException>(() => world.Notifier.NotifyAsync(Requested("maria", "fin"), messageId, Ct));
        world.Email.Sent.Select(m => m.ToAddress).ShouldBe(["fin@acme.test"], "the other recipient was still tried");

        world.Email.FailFor.Clear();
        await world.Notifier.NotifyAsync(Requested("maria", "fin"), messageId, Ct);

        world.Email.Sent.Select(m => m.ToAddress).ShouldBe(["fin@acme.test", "maria@acme.test"]);
        world.Bells.Items.Count.ShouldBe(2, "the retry added no second bell item");
    }

    [Fact]
    public async Task A_user_without_an_address_gets_the_bell_item_only()
    {
        var world = new World();

        await world.Notifier.NotifyAsync(Requested("nomail"), Guid.NewGuid(), Ct);

        world.Bells.Items.ShouldHaveSingleItem().User.ShouldBe("nomail");
        world.Email.Sent.ShouldBeEmpty();
    }

    /// <summary>The notifier with in-memory bell, pushes, directory, email and dedup.</summary>
    private sealed class World
    {
        public MemoryBells Bells { get; } = new();

        public Pushes Pushes { get; } = new();

        public Mailbox Email { get; } = new();

        public PersonalNotifier Notifier { get; }

        public World() =>
            Notifier = new PersonalNotifier(
                Bells, Pushes, new Directory(), Email, new MemoryOnce(), new NoCache(),
                Options.Create(new SmtpOptions { Host = "smtp", From = "no-reply@app.test", AppBaseUrl = "https://app.test/" }),
                NullLogger<PersonalNotifier>.Instance);
    }

    private sealed class MemoryBells : IBellStore
    {
        public List<(string Tenant, string User, BellItem Item)> Items { get; } = [];

        public Task<bool> AddAsync(string tenantId, string userId, BellItem item, CancellationToken cancellationToken)
        {
            if (Items.Any(i => i.Tenant == tenantId && i.User == userId && i.Item.Id == item.Id))
            {
                return Task.FromResult(false);
            }

            Items.Add((tenantId, userId, item));
            return Task.FromResult(true);
        }

        public Task<BellList> ListAsync(string tenantId, string userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> MarkReadAsync(string tenantId, string userId, string itemId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkAllReadAsync(string tenantId, string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Pushes : ILivePushes
    {
        public List<(string Tenant, string User, BellItem Item)> ToUsers { get; } = [];

        public Task PushToTenantAsync(string tenantId, IReadOnlyList<EntityChanged> changes, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PushViewersAsync(string tenantId, PresenceTarget target, IReadOnlyList<Viewer> viewers, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PushOnlineAsync(string tenantId, IReadOnlyList<Viewer> online, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PushToUserAsync(string tenantId, string userId, BellItem item, CancellationToken cancellationToken)
        {
            ToUsers.Add((tenantId, userId, item));
            return Task.CompletedTask;
        }
    }

    private sealed class Directory : IAccessDirectory
    {
        public ValueTask<DirectoryUser?> GetUserAsync(string userId, string tenantId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(userId == "nomail" ? new DirectoryUser(userId, "No Mail", "") : new DirectoryUser(userId, userId, $"{userId}@acme.test"))!;

        public ValueTask<EffectiveAccess> GetEffectiveAccessAsync(
            string userId, IReadOnlyCollection<string> roles, string tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<CostCenterApprovers> GetApproversAsync(string costCenterCode, string tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetDisplayNameAsync(string userId, string tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Mailbox : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public HashSet<string> FailFor { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (FailFor.Contains(message.ToAddress))
            {
                throw new IOException("SMTP server went away");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryOnce : IEmailOnce
    {
        private readonly HashSet<string> _claimed = [];

        public Task<bool> TryClaimAsync(string key, TimeSpan window, CancellationToken cancellationToken) => Task.FromResult(_claimed.Add(key));

        public Task ReleaseAsync(string key, CancellationToken cancellationToken)
        {
            _claimed.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class NoCache : ICacheInvalidator
    {
        public Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAfterCommitAsync(IEnumerable<string> tags) => Task.CompletedTask;
    }
}
