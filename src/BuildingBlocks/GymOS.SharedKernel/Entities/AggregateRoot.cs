using GymOS.SharedKernel.Events;

namespace GymOS.SharedKernel.Entities;

/// <summary>
/// Base type for aggregate roots — the only objects that raise domain events (AD-13).
/// Events are raised inside domain methods so an invariant and its event can never
/// drift apart; the persistence layer drains them via <see cref="DequeueDomainEvents"/>
/// into the transactional outbox within the same transaction as the state change.
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<DomainEvent> _domainEvents = [];

    protected AggregateRoot(Guid id)
        : base(id)
    {
    }

    /// <summary>Parameterless constructor reserved for ORM materialization.</summary>
    protected AggregateRoot()
    {
    }

    /// <summary>Events raised since the last dequeue, in the order they were raised.</summary>
    public IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Returns all pending events and clears the collection. Called by the outbox
    /// interceptor at save time; domain and application code never call this.
    /// </summary>
    public IReadOnlyList<DomainEvent> DequeueDomainEvents()
    {
        if (_domainEvents.Count == 0)
        {
            return [];
        }

        var dequeued = _domainEvents.ToList();
        _domainEvents.Clear();
        return dequeued;
    }
}
