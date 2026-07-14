namespace Whisper;

public static class Whispers
{
    private static readonly AsyncLocal<Murmur> _domainEvents = new();

    public static IDisposable CreateScope()
    {
        return new DomainEventScope(_domainEvents);
    }

    public static void About(IDomainEvent domainEvent)
    {
        _domainEvents.Value ??= [];
        _domainEvents.Value!.Add(domainEvent);
    }

    public static IDomainEvent[] Peek()
    {
        return _domainEvents.Value?.Snapshot() ?? [];
    }

    public static IDomainEvent[] GetAndClearEvents()
    {
        var events = _domainEvents.Value;

        return events is null ? [] : events.DrainAll();
    }
}
