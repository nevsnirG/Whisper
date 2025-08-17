using Hermes.Abstractions;
using Hermes.Core;
using MediatR;

namespace Hermes.MediatR;
internal sealed class MediatRDispatcher(IMediator mediator) : IDispatchDomainEvents
{
    public Task Dispatch(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        return mediator.Publish(domainEvent, cancellationToken);
    }

    public async Task Dispatch(IDomainEvent[] domainEvents, CancellationToken cancellationToken)
    {
        foreach (var domainEvent in domainEvents)
        {
            await mediator.Publish(domainEvent, cancellationToken);
        }
    }
}