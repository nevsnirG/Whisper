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
        var events = _domainEvents.Value;

        if (events is null || events.Count == 0)
            return [];

        var drained = events.ToArray();
        events.Clear();
        return drained;
    }
}
