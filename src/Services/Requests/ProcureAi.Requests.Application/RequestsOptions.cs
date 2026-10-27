using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Requests.Application;

/// <summary>Configuration section <c>Requests</c>.</summary>
public sealed class RequestsOptions
{
    public const string SectionName = "Requests";

    /// <summary>
    /// How an outcome handler (RequestApproved / RequestRejected) that lost a race with a write of the same request (a cancel)
    /// is retried; the retry sees the winner's state. Set in appsettings (<c>Requests:ConcurrencyRetry</c>).
    /// </summary>
    [Required, ValidateObjectMembers]
    public ConcurrencyRetryOptions ConcurrencyRetry { get; set; } = new();
}
