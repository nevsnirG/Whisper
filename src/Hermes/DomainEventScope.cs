namespace Hermes;

internal sealed record class DomainEventScope : IDomainEventScope
{
    public int Id { get; }
    internal IDomainEventScope? Child { get; set; }

    private DomainEventBag _domainEvents = [];

    internal DomainEventScope(int id)
    {
        Id = id;
    }

    public void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }

    public IDomainEvent[] Peek()
    {
        return [.. _domainEvents];
    }

    public IDomainEvent[] GetAndClearEvents()
    {
        var events = new List<IDomainEvent>(_domainEvents);

        if (Child is not null)
            events.AddRange(Child.GetAndClearEvents());

        _domainEvents = [];
        return [.. events];
    }

    public void Dispose()
    {
        DomainEventTracker.ExitScope(this);
    }
}