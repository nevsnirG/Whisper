using Hermes.Abstractions;
using Hermes.Outbox.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Outbox;
internal sealed class OutboxWorker(IOutboxStore outboxStore,
                                   IDomainEventSerializer domainEventSerializer,
                                   TimeProvider timeProvider,
                                   OutboxInstallerAwaiter outboxInstallerAwaiter,
                                   IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    private const int Delay = 1 * 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await outboxInstallerAwaiter.WaitForCompletion(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessNextBatch(stoppingToken);
#pragma warning disable CA2016 // Forward the 'CancellationToken' parameter to methods
            await Task.Delay(Delay, stoppingToken)
                .ContinueWith(_ => { });
#pragma warning restore CA2016 // Forward the 'CancellationToken' parameter to methods
        }
    }

    private async Task ProcessNextBatch(CancellationToken cancellationToken)
    {
        var outboxRecordBatch = await outboxStore.ReadNextBatch(cancellationToken);

        if (outboxRecordBatch is [])
        {
            return;
        }

        foreach (var outboxRecord in outboxRecordBatch)
        {
            await ProcessOutboxRecord(outboxRecord, cancellationToken);
        }

        await MarkAsDispatched(outboxRecordBatch, cancellationToken);
    }

    private async Task ProcessOutboxRecord(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        var domainEvent = DeserializeDomainEvent(outboxRecord);
        await DispatchDomainEvent(domainEvent, cancellationToken);
    }

    private IDomainEvent DeserializeDomainEvent(OutboxRecord outboxRecord)
    {
        return domainEventSerializer.Deserialize(outboxRecord.Payload, outboxRecord.AssemblyQualifiedType);
    }

    private async Task<IServiceScope> DispatchDomainEvent(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dispatchers = scope.ServiceProvider.GetKeyedServices<IDispatchDomainEvents>(IHermesBuilderExtensions.ServiceKey)
            .Where(s => s is not OutboxDispatcher);

        foreach (var dispatcher in dispatchers)
        {
            await dispatcher.Dispatch(domainEvent, cancellationToken);
        }

        return scope;
    }

    private async Task MarkAsDispatched(OutboxRecord[] outboxRecordBatch, CancellationToken cancellationToken)
    {
        foreach (var outboxRecord in outboxRecordBatch)
        {
            outboxRecord.DispatchedAtUtc = timeProvider.GetUtcNow();
        }

        await outboxStore.SetDispatchedAt(outboxRecordBatch, cancellationToken);
    }
}