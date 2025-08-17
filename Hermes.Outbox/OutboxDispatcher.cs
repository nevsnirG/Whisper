using Hermes.Abstractions;
using Hermes.Core;
using Hermes.Outbox.Abstractions;

namespace Hermes.Outbox;
internal sealed class OutboxDispatcher(IOutboxStore outboxStore, IDomainEventSerializer domainEventSerializer) : IDispatchDomainEvents
{
    public Task Dispatch(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var outboxRecord = CreateOutboxRecord(domainEvent);
        return outboxStore.Add(outboxRecord, cancellationToken);
    }

    public Task Dispatch(IDomainEvent[] domainEvents, CancellationToken cancellationToken)
    {
        var outboxRecords = domainEvents
            .Select(CreateOutboxRecord)
            .ToArray();
        return outboxStore.Add(outboxRecords, cancellationToken);
    }

    private OutboxRecord CreateOutboxRecord(IDomainEvent domainEvent)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            AssemblyQualifiedType = domainEvent.GetType().AssemblyQualifiedName!,
            Payload = domainEventSerializer.Serialize(domainEvent),
        };
    }
}