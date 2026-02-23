using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox;

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
#pragma warning disable CA2016
            await Task.Delay(Delay, stoppingToken)
                .ContinueWith(_ => { });
#pragma warning restore CA2016
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
    }

    private async Task ProcessOutboxRecord(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        var domainEvent = domainEventSerializer.Deserialize(outboxRecord.Payload, outboxRecord.AssemblyQualifiedType);
        using var scope = serviceScopeFactory.CreateScope();
        var dispatchers = scope.ServiceProvider.GetKeyedServices<IDispatchDomainEvents>(IWhisperBuilderExtensions.ServiceKey)
            .Where(s => s is not OutboxDispatcher);

        foreach (var dispatcher in dispatchers)
        {
            await dispatcher.Dispatch(domainEvent, cancellationToken);
        }
        await outboxStore.SetDispatchedAt(outboxRecord, timeProvider.GetUtcNow(), cancellationToken);
    }
}