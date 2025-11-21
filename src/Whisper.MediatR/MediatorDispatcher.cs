using Whisper.Abstractions;
using MediatR;

namespace Whisper.MediatR;
internal sealed class MediatorDispatcher(IMediator mediator) : IDispatchDomainEvents
{
    public Task Dispatch(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        return mediator.Publish(domainEvent, cancellationToken);
    }

    public Task Dispatch(IDomainEvent[] domainEvents, CancellationToken cancellationToken)
    {
        return Task.WhenAll(
            domainEvents.Select(d => mediator.Publish(d, cancellationToken)));
    }
}