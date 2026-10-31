using Whisper.Abstractions;
using MediatR;

namespace Whisper.MediatR;
internal sealed class MediatorDispatcher(IMediator mediator) : IDispatchDomainEvents
{
    public Task Dispatch(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        return mediator.Publish(domainEvent, cancellationToken);
    }

    public async Task Dispatch(IDomainEvent[] domainEvents, CancellationToken cancellationToken)
    {
        // One at a time, in raised order: handlers commonly share scoped services, such as an
        // EF Core DbContext, that do not support concurrent use.
        foreach (var domainEvent in domainEvents)
            await mediator.Publish(domainEvent, cancellationToken);
    }
}