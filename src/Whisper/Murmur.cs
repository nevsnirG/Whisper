using System.Collections;

namespace Whisper;

internal sealed class Murmur : IEnumerable<IDomainEvent>
{
    private readonly List<IDomainEvent> _domainEvents = [];
    private readonly object _gate = new();

    public void Add(IDomainEvent domainEvent)
    {
        lock (_gate)
        {
            _domainEvents.Add(domainEvent);
        }
    }

    public IDomainEvent[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _domainEvents];
        }
    }

    public IDomainEvent[] DrainAll()
    {
        lock (_gate)
        {
            if (_domainEvents.Count == 0)
                return [];

            var drained = _domainEvents.ToArray();
            _domainEvents.Clear();
            return drained;
        }
    }

    public IEnumerator<IDomainEvent> GetEnumerator()
    {
        return ((IEnumerable<IDomainEvent>)Snapshot()).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}