namespace ProcureAi.Assistant.Application.Ingestion;

/// <summary>
/// Ingest one version of a policy document (a durable local message, queued with the upload or the replacement, and by the demo seed):
/// extract, guard, plan, embed (reusing what is unchanged), then make the version current. A message for a version that is no longer
/// the newest, or of a removed document, does nothing but clear what it stored.
/// </summary>
public sealed record IngestPolicyDocument(Guid DocumentId, int Version);

/// <summary>
/// The model provider failed for a reason that may pass (see <see cref="Ai.IAiFailures"/>). Thrown by the ingestion so Wolverine schedules
/// the next retry (<c>Assistant:Ingestion:RetryDelays</c>); on the last attempt the version is marked <c>Failed</c> instead.
/// </summary>
public sealed class AiProviderUnavailableException(Exception inner)
    : Exception("The AI model is not available right now.", inner);
