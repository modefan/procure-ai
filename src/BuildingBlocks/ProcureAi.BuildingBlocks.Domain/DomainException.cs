namespace ProcureAi.BuildingBlocks.Domain;

/// <summary>Thrown when a domain rule is broken.</summary>
public sealed class DomainException(string message) : Exception(message);
