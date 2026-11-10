namespace Whisper;

/// <summary>
/// Thrown by <see cref="Whispers.About"/> when a domain event is raised while domain events are being dispatched
/// (see <see cref="Whispers.BeginDispatch"/>). Domain events are dispatched once, after the use case that raised them,
/// so a handler cannot add to the dispatch it is part of. A handler that has follow-up work starts a new use case
/// instead (for example, by sending a command), which gets its own scope.
/// </summary>
public sealed class DomainEventRaisedDuringDispatchException(IDomainEvent domainEvent) : InvalidOperationException(
    $"Domain event {domainEvent.GetType().Name} was raised while domain events were being dispatched. " +
    "Domain events are dispatched once, after the use case that raised them, so a handler cannot raise more in the scope it is handled in. " +
    "Start a new use case instead (for example, send a command), which gets its own scope.")
{
    /// <summary>The domain event that was rejected.</summary>
    public IDomainEvent DomainEvent { get; } = domainEvent;
}
