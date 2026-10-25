namespace ProcureAi.Requests.Application.Abstractions;

/// <summary>
/// Who is calling and for which tenant. The Api adapts its authenticated user to this, so Application and
/// Infrastructure do not depend on the web building blocks.
/// </summary>
public interface ICallerContext
{
    string UserId { get; }

    string? TenantId { get; }
}
