namespace ProcureAi.BuildingBlocks.Domain;

public abstract class Entity<TId>
{
    private readonly List<object> _domainEvents = [];

    public TId Id { get; protected set; } = default!;

    public IReadOnlyList<object> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(object @event) => _domainEvents.Add(@event);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
