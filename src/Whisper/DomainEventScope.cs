using System.Diagnostics.CodeAnalysis;

namespace Whisper;

internal sealed record class DomainEventScope : IDisposable
{
    private readonly AsyncLocal<Murmur> _domainEvents;
    private readonly Murmur _oldValue;

    internal DomainEventScope([NotNull] AsyncLocal<Murmur> domainEvents)
    {
        _domainEvents = domainEvents;
        _oldValue = domainEvents.Value!;
        domainEvents!.Value = [];
    }

    public void Dispose()
    {
        _domainEvents.Value = _oldValue;
    }
}