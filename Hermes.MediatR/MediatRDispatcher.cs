using Hermes.Abstractions;
using Hermes.Core;

namespace Hermes.MediatR;

internal sealed class MediatRDispatcher : IDispatchDomainEvents
{
    public Task Dispatch(IDomainEvent domainEvent)
    {
        throw new NotImplementedException();
    }

    public Task Dispatch(IDomainEvent[] domainEvents)
    {
        throw new NotImplementedException();
    }
}