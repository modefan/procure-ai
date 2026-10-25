namespace ProcureAi.BuildingBlocks.Application;

/// <summary>How the current work reached the service.</summary>
public enum CallerKind
{
    /// <summary>Nobody called: startup, seeding, a background job.</summary>
    System,

    /// <summary>An HTTP request with a user's token.</summary>
    User,

    /// <summary>An HTTP request with the admin API key (Claude's testing access).</summary>
    AdminApiKey,

    /// <summary>An HTTP request from another service with its service-account token.</summary>
    Service,

    /// <summary>A message consumed from the broker.</summary>
    Message,
}

/// <summary>
/// Who a handler acts for and who actually performed it. The Api adapts the HTTP user or the message headers to this,
/// so Application and Infrastructure depend on neither the web nor the messaging building blocks.
/// </summary>
public interface ICallerContext
{
    /// <summary>The person the action is for; <see cref="SystemCaller.Id"/> when nobody.</summary>
    string UserId { get; }

    /// <summary>Who performed it: a user ID, <c>claude-admin</c>, or <c>service:&lt;name&gt;</c>.</summary>
    string ActorId { get; }

    string? TenantId { get; }

    /// <summary>The service that made the call or sent the message, when known.</summary>
    string? CallingService { get; }

    CallerKind Kind { get; }
}

public static class SystemCaller
{
    public const string Id = "system";

    public static string Service(string name) => "service:" + name;
}
