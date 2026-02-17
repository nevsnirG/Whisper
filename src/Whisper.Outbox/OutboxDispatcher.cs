using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox;

internal sealed class OutboxDispatcher(IOutboxStore outboxStore,
                                       IDomainEventSerializer domainEventSerializer,
                                       TimeProvider timeProvider,
                                       IUuidProvider uuidProvider) : IDispatchDomainEvents
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

        if (outboxRecords.Length == 0)
            return Task.CompletedTask;

        return outboxStore.Add(outboxRecords, cancellationToken);
    }

    private OutboxRecord CreateOutboxRecord(IDomainEvent domainEvent)
    {
        return new()
        {
            Id = uuidProvider.Provide(),
            EnqueuedAtUtc = timeProvider.GetUtcNow(),
            AssemblyQualifiedType = domainEvent.GetType().AssemblyQualifiedName!,
            Payload = domainEventSerializer.Serialize(domainEvent),
        };
    }
}

internal sealed class BlockingOutboxDispatcher(OutboxInstallerAwaiter outboxInstallerAwaiter, IDispatchDomainEvents innerDispatcher) : IDispatchDomainEvents
{
    public async Task Dispatch(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        await outboxInstallerAwaiter.WaitForCompletion(cancellationToken);
        await innerDispatcher.Dispatch(domainEvent, cancellationToken);
    }

    public async Task Dispatch(IDomainEvent[] domainEvents, CancellationToken cancellationToken)
    {
        await outboxInstallerAwaiter.WaitForCompletion(cancellationToken);
        await innerDispatcher.Dispatch(domainEvents, cancellationToken);
    }
}