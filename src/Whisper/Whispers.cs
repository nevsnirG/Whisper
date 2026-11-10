namespace Whisper;

public static class Whispers
{
    private static readonly AsyncLocal<Murmur?> _domainEvents = new();

    public static IDisposable CreateScope()
    {
        return new DomainEventScope(_domainEvents, []);
    }

    /// <summary>
    /// Marks the current flow as dispatching domain events. Until the returned scope is disposed, <see cref="About"/>
    /// throws <see cref="DomainEventRaisedDuringDispatchException"/>: domain events are dispatched once, after the use
    /// case that raised them, so a handler cannot add to the dispatch it is part of. A scope created inside it
    /// (<see cref="CreateScope"/>, which is what sending a command through the MediatR integration does) is a new use
    /// case and collects events again.
    /// </summary>
    public static IDisposable BeginDispatch()
    {
        return new DomainEventScope(_domainEvents, Murmur.Dispatching);
    }

    public static void About(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

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
