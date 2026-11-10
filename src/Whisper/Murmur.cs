using System.Collections;

namespace Whisper;

internal sealed class Murmur : IEnumerable<IDomainEvent>
{
    /// <summary>
    /// The collector that is active while domain events are being dispatched. It accepts nothing, so it holds no
    /// state and one instance serves every dispatch.
    /// </summary>
    public static readonly Murmur Dispatching = new(acceptsEvents: false);

    private readonly List<IDomainEvent> _domainEvents = [];
    private readonly object _gate = new();
    private readonly bool _acceptsEvents;

    public Murmur() : this(acceptsEvents: true)
    {
    }

    private Murmur(bool acceptsEvents)
    {
        _acceptsEvents = acceptsEvents;
    }

    public void Add(IDomainEvent domainEvent)
    {
        if (!_acceptsEvents)
            throw new DomainEventRaisedDuringDispatchException(domainEvent);

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
