namespace ProcureAi.Audit.Application.Dtos;

/// <summary>One line of a request's history: what happened, for whom, when, in words.</summary>
public sealed record BusinessEventDto(string Type, string UserId, DateTimeOffset OccurredAt, string Summary);
