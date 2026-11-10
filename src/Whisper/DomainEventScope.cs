namespace Whisper;

internal sealed class DomainEventScope : IDisposable
{
    private readonly AsyncLocal<Murmur?> _domainEvents;
    private readonly Murmur? _oldValue;

    internal DomainEventScope(AsyncLocal<Murmur?> domainEvents, Murmur collector)
    {
        _domainEvents = domainEvents;
        _oldValue = domainEvents.Value;
        domainEvents.Value = collector;
    }

    public void Dispose()
    {
        _domainEvents.Value = _oldValue;
    }
}
