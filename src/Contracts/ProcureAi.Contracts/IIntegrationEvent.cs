namespace ProcureAi.Contracts;

/// <summary>
/// A fact one service publishes and others consume over the broker. Commands (<c>ReserveBudget</c>, <c>ReleaseBudget</c>)
/// are addressed to one service and do not implement this.
/// </summary>
public interface IIntegrationEvent
{
    string TenantId { get; }

    DateTimeOffset OccurredAt { get; }
}
