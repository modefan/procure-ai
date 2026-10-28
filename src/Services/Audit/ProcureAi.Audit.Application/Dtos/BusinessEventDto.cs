namespace ProcureAi.Audit.Application.Dtos;

/// <summary>
/// One line of a request's history: what happened, for whom, when, in words. <paramref name="StepNo"/> is the approval step the
/// event is about (approval events only) and <paramref name="ActorName"/> the display name of the user who acted (null for the
/// system or when Access could not name them), so a client never has to read them out of <paramref name="Summary"/>.
/// </summary>
public sealed record BusinessEventDto(string Type, string UserId, DateTimeOffset OccurredAt, string Summary, int? StepNo = null, string? ActorName = null);

/// <summary>One entry of the recent activity feed: a business event with the request it concerns and who it happened for.</summary>
public sealed record ActivityDto(
    DateTimeOffset OccurredAt, string EventType, Guid? RequestId, string? RequestNumber, string Summary, string? ActorName);
