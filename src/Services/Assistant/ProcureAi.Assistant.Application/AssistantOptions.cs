using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Ingestion;

namespace ProcureAi.Assistant.Application;

/// <summary>The <c>Assistant</c> section: the service's own limits (plan 05 rulings 7 and 8). Later tasks add retrieval, ingestion and uploads.</summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    [Required, ValidateObjectMembers]
    public ToolOptions Tools { get; set; } = new();

    [Required, ValidateObjectMembers]
    public QuotaOptions Quota { get; set; } = new();

    /// <summary>How documents are cut into chunks (<c>Assistant:Chunking</c>).</summary>
    [Required, ValidateObjectMembers]
    public ChunkingOptions Chunking { get; set; } = new();
}

public sealed class ToolOptions
{
    /// <summary>The most tool calls one question may make; then the model answers with what it has (ruling 8).</summary>
    [Range(1, 20)]
    public int MaxCallsPerQuestion { get; set; } = 5;
}

public sealed class QuotaOptions
{
    /// <summary>Questions per user per South African calendar day (ruling 7).</summary>
    [Range(1, 10_000)]
    public int QuestionsPerDay { get; set; } = 50;
}
