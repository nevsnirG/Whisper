using Hermes.Abstractions;
using Hermes.Core;

namespace Hermes.Outbox;

internal sealed class OutboxDispatcher(IOutboxPersister outboxPersister) : IDispatchDomainEvents
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
