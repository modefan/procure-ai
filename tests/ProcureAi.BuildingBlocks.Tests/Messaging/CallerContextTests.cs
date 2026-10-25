using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Auth;
using Shouldly;
using Wolverine;

namespace ProcureAi.BuildingBlocks.Tests.Messaging;

/// <summary>One row per line of the caller table: who a handler acts for, who performed it, and how it arrived.</summary>
public sealed class CallerContextTests
{
    private const string Service = "requests-api";

    private sealed record Case(
        ICurrentUser User, Envelope? Message, string UserId, string ActorId, string? TenantId, string? CallingService, CallerKind Kind);

    private static Envelope Message(params (string Name, string Value)[] headers)
    {
        var envelope = new Envelope();
        foreach (var (name, value) in headers)
        {
            envelope.Headers[name] = value;
        }

        return envelope;
    }

    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["http jwt user"] = new(
            new FakeUser("tom", "acme"), null, "tom", "tom", "acme", null, CallerKind.User),
        ["http admin key"] = new(
            new FakeUser("claude-admin", "acme", admin: true), null, "claude-admin", "claude-admin", "acme", null, CallerKind.AdminApiKey),
        ["http admin key on behalf of tom"] = new(
            new FakeUser("claude-admin", "acme", admin: true, onBehalfOf: "tom"),
            null, "tom", "claude-admin", "acme", null, CallerKind.AdminApiKey),
        ["http service token on behalf of tom"] = new(
            new FakeUser("svc-account", "acme", service: true, callingService: "approvals-api", onBehalfOf: "tom"),
            null, "tom", "service:approvals-api", "acme", "approvals-api", CallerKind.Service),
        ["message with user"] = new(
            new FakeUser(), Message((CallerHeaders.UserId, "tom"), (CallerHeaders.TenantId, "acme")),
            "tom", "service:requests-api", "acme", null, CallerKind.Message),
        ["message without user"] = new(
            new FakeUser(), Message((CallerHeaders.TenantId, "acme")),
            "system", "service:requests-api", "acme", null, CallerKind.Message),
        ["message from another service"] = new(
            new FakeUser(), Message((CallerHeaders.UserId, "tom"), (CallerHeaders.CallingService, "approvals-api")),
            "tom", "service:requests-api", null, "approvals-api", CallerKind.Message),
        ["nothing (startup, seed)"] = new(
            new FakeUser(), null, "system", "service:requests-api", null, null, CallerKind.System),
    };

    public static TheoryData<string> Names => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(Names))]
    public void Resolves_user_actor_tenant_and_kind(string name)
    {
        var expected = Cases[name];
        var caller = new CallerContext(expected.User, new IncomingMessage { Envelope = expected.Message }, new ServiceIdentity(Service));

        caller.UserId.ShouldBe(expected.UserId);
        caller.ActorId.ShouldBe(expected.ActorId);
        caller.TenantId.ShouldBe(expected.TenantId);
        caller.CallingService.ShouldBe(expected.CallingService);
        caller.Kind.ShouldBe(expected.Kind);
    }

    [Fact]
    public void Http_request_wins_over_message_headers()
    {
        // A handler invoked inline inside an HTTP request: the authenticated user is the caller, not a stray header.
        var caller = new CallerContext(
            new FakeUser("tom", "acme"),
            new IncomingMessage { Envelope = Message((CallerHeaders.UserId, "mallory"), (CallerHeaders.TenantId, "globex")) },
            new ServiceIdentity(Service));

        caller.UserId.ShouldBe("tom");
        caller.TenantId.ShouldBe("acme");
        caller.Kind.ShouldBe(CallerKind.User);
    }

    private sealed class FakeUser(
        string? userId = null, string? tenant = null, bool admin = false, bool service = false,
        string? callingService = null, string? onBehalfOf = null) : ICurrentUser
    {
        public bool IsAuthenticated => userId is not null;
        public string UserId => userId ?? CurrentUser.AnonymousUserId;
        public string? TenantId => tenant;
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();
        public bool IsAdminApiKey => admin;
        public bool IsServiceAccount => service;
        public string? CallingService => callingService;
        public string? OnBehalfOfUser => onBehalfOf;
    }
}
