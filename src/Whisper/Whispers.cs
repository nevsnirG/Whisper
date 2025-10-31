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
        if (_domainEvents.Value is null)
            return [];

        return [.. _domainEvents.Value];
    }

    public static IDomainEvent[] GetAndClearEvents()
    {
        var events = _domainEvents.Value?.ToArray() ?? [];
        _domainEvents.Value = [];
        return events;
    }
}
