namespace ClinIQ.Domain.Common;

public abstract class Entity
{
    public Guid Id {get; private set;}

    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected Entity() {}

    protected Entity(Guid id)
    {
        if(id == Guid.Empty)
            throw new ArgumentException("Entity ID cannot be empty.", nameof(id));

        Id = id;
        // TODO: we need to know and understand why we don't write the assignment like this
        //       Id = id == Guid.Empty ? Guid.NewGuid() : id;

    }

    protected void AddDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}
