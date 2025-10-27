namespace Whisper;

public interface IDomainEventScope : IDisposable
{
    int Id { get; }
    IDomainEvent[] GetAndClearEvents();
    IDomainEvent[] Peek();
    void RaiseDomainEvent(IDomainEvent domainEvent);
}
