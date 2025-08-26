using System.Collections;

namespace Hermes.Core;
internal sealed class DomainEventBag : IEnumerable<IDomainEvent>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public void Add(IDomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }

    public IEnumerator<IDomainEvent> GetEnumerator()
    {
        return _domainEvents.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}