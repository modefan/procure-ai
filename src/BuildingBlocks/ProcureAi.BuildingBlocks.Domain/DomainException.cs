namespace ProcureAi.BuildingBlocks.Domain;

/// <summary>Thrown when a domain rule is broken.</summary>
public class DomainException(string message) : Exception(message);
