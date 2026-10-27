using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Infrastructure;

/// <summary>Request numbers (PR-1043) come from the tenant sequence <see cref="SequenceName"/>, in the request's own transaction.</summary>
public sealed class RequestNumberGenerator(ITenantSequence sequences) : IRequestNumberGenerator
{
    public const string SequenceName = "PR";

    public async Task<RequestNumber> NextAsync(string tenantId, CancellationToken cancellationToken) =>
        RequestNumber.From(await sequences.NextAsync(tenantId, SequenceName, cancellationToken));
}
