namespace ProcureAi.Assistant.Domain;

/// <summary>A tool the model called while answering (plan 05 Task 5b): what it asked for and how it went. No result data is kept.</summary>
/// <param name="Id">The model's call ID.</param>
/// <param name="Arguments">The arguments as JSON.</param>
/// <param name="Summary">A short summary of the result ("3 requests").</param>
public sealed record ToolStep(string Id, string Name, string Arguments, bool Ok, string Summary, long DurationMs);
