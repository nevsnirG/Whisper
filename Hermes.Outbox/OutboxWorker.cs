using Hermes.Abstractions;
using Hermes.Core;
using Hermes.Outbox.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hermes.Outbox;
internal sealed class OutboxWorker(IOutboxStore outboxStore,
                                   IDomainEventSerializer domainEventSerializer,
                                   TimeProvider timeProvider,
                                   IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    private const int DelayInSeconds = 1 * 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessNextBatch(stoppingToken);
            await Task.Delay(DelayInSeconds, stoppingToken);
        }
    }

    private async Task ProcessNextBatch(CancellationToken cancellationToken)
    {
        var outboxRecordBatch = await outboxStore.ReadNextBatch(cancellationToken);

        foreach (var outboxRecord in outboxRecordBatch)
        {
            await ProcessOutboxRecord(outboxRecord, cancellationToken);
        }
    }

    private async Task ProcessOutboxRecord(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        var domainEvent = DeserializeDomainEvent(outboxRecord);
        await DispatchDomainEvent(domainEvent, cancellationToken);
        await UpdateDispatchedAtUtc(outboxRecord, cancellationToken);
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

    private async Task UpdateDispatchedAtUtc(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        outboxRecord.DispatchedAtUtc = timeProvider.GetUtcNow();
        await outboxStore.Update(outboxRecord, cancellationToken);
    }
}