using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Gateway.Configuration;

/// <summary>The <c>Gateway:ClientLogs</c> section: how the gateway treats the browser's log batches.</summary>
public sealed class ClientLogOptions
{
    public const string SectionName = "Gateway:ClientLogs";

    /// <summary>Batches accepted per minute from one session (signed-in user) or, without a session, one client IP.</summary>
    [Range(1, 100_000)]
    public int PermitPerMinute { get; set; } = 60;

    /// <summary>A message, source or data text longer than this is cut.</summary>
    [Range(50, 10_000)]
    public int MaxMessageLength { get; set; } = 2000;

    /// <summary>
    /// Entries logged per batch. Later entries are dropped and one warning says how many (the batch is still accepted:
    /// the browser cannot act on a refusal, and its earliest entries are the most useful).
    /// </summary>
    [Range(1, 1000)]
    public int MaxEntriesPerBatch { get; set; } = 50;
}
