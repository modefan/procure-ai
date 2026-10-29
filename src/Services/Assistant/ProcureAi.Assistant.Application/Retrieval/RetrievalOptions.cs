using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Assistant.Application.Retrieval;

/// <summary>The <c>Assistant:Retrieval</c> section: how hybrid search finds the sources of an answer (plan 05 ruling 8).</summary>
public sealed class RetrievalOptions
{
    public const string SectionPath = AssistantOptions.SectionName + ":Retrieval";

    /// <summary>How many sources an answer gets.</summary>
    [Range(1, 50)]
    public int TopK { get; set; } = 6;

    /// <summary>How many candidates each search arm (vector and full text) brings to the fusion.</summary>
    [Range(1, 200)]
    public int CandidatesPerArm { get; set; } = 20;

    /// <summary>The <c>k</c> of reciprocal rank fusion: score = Σ 1 / (k + rank).</summary>
    [Range(1, 1000)]
    public int RrfK { get; set; } = 60;

    /// <summary>Whether the fused candidates are reordered by the <c>utility</c> model (<see cref="ModelReranker"/>) before the top K.</summary>
    public bool Rerank { get; set; }
}
