using System.Collections;

namespace Whisper;
internal sealed class Murmur : IEnumerable<IDomainEvent>
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