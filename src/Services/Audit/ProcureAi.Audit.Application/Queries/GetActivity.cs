using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Requests;

namespace ProcureAi.Audit.Application.Queries;

/// <summary>The latest business events of the caller tenant, newest first.</summary>
public sealed record GetActivity(int Limit);

public static class GetActivityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Each event with its one-line summary, the request it is about (ID and number, the number from that request's
    /// <c>RequestSubmitted</c>) and the name of the user it happened for. Names come from Access (cached); when Access cannot
    /// answer in time the summaries use user IDs and <c>ActorName</c> is null.
    /// </summary>
    public static async Task<IReadOnlyList<ActivityDto>> Handle(
        GetActivity query, ICallerContext caller, IAuditStore store, IAccessDirectory access, IOptions<AuditOptions> options,
        ILogger<GetActivity> logger, CancellationToken cancellationToken)
    {
        var tenant = caller.TenantId ?? throw new NotFoundException("No tenant.");
        var latest = await store.LatestAsync(tenant, query.Limit, cancellationToken);
        if (latest.Count == 0)
        {
            return [];
        }

        var numbers = await NumbersAsync(tenant, latest, store, cancellationToken);
        var summaries = BusinessEventSummaries.Read(latest);
        var names = await GetRequestHistoryHandler.DisplayNamesAsync(
            summaries.UserIds, tenant, access, options.Value.DisplayNameTimeout, logger, cancellationToken);
        return
        [
            .. summaries.Events.Select(e => new ActivityDto(
                e.Stored.OccurredAt,
                e.Stored.Type,
                e.Stored.SubjectId,
                e.Stored.SubjectId is { } id ? numbers.GetValueOrDefault(id) : null,
                summaries.Summarize(e, names),
                names.GetValueOrDefault(e.Stored.UserId))),
        ];
    }

    private static async Task<Dictionary<Guid, string>> NumbersAsync(
        string tenant, IReadOnlyList<Domain.BusinessEvent> events, IAuditStore store, CancellationToken cancellationToken)
    {
        var subjects = events.Where(e => e.SubjectId is not null).Select(e => e.SubjectId!.Value).Distinct().ToList();
        var submissions = await store.SubmissionsAsync(tenant, subjects, cancellationToken);
        var numbers = new Dictionary<Guid, string>();
        foreach (var stored in submissions)
        {
            if (stored.SubjectId is { } id && TryNumber(stored.Payload) is { } number)
            {
                numbers[id] = number;
            }
        }

        return numbers;
    }

    private static string? TryNumber(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<RequestSubmitted>(payload, Json)?.Number;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
