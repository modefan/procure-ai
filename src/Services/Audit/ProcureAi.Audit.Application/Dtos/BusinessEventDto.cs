namespace ProcureAi.Audit.Application.Dtos;

/// <summary>One line of a request's history: what happened, for whom, when, in words.</summary>
public sealed record BusinessEventDto(string Type, string UserId, DateTimeOffset OccurredAt, string Summary);

/// <summary>One entry of the recent activity feed: a business event with the request it concerns and who it happened for.</summary>
public sealed record ActivityDto(
    DateTimeOffset OccurredAt, string EventType, Guid? RequestId, string? RequestNumber, string Summary, string? ActorName);
