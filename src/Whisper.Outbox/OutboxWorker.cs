using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox;

internal sealed class OutboxWorker(IDomainEventSerializer domainEventSerializer,
                                   TimeProvider timeProvider,
                                   OutboxInstallerAwaiter outboxInstallerAwaiter,
                                   IOptions<OutboxWorkerOptions> outboxWorkerOptions,
                                   IServiceScopeFactory serviceScopeFactory,
                                   ILogger<OutboxWorker> logger) : BackgroundService
{
    private readonly OutboxWorkerOptions _outboxWorkerOptions = outboxWorkerOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await outboxInstallerAwaiter.WaitForCompletion(stoppingToken);

        logger.LogInformation("Outbox worker started with batch size {BatchSize} and polling interval {PollingIntervalMs}ms.",
            _outboxWorkerOptions.BatchSize, _outboxWorkerOptions.PollingIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextBatch(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while processing the outbox batch.");
            }

#pragma warning disable CA2016
            await Task.Delay(_outboxWorkerOptions.PollingIntervalMs, stoppingToken)
                .ContinueWith(_ => { });
#pragma warning restore CA2016
        }
    }

    private async Task ProcessNextBatch(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var outboxRecordBatch = await outboxStore.ReadNextBatch(_outboxWorkerOptions.BatchSize, cancellationToken);

        if (outboxRecordBatch is [])
            return;

        foreach (var outboxRecord in outboxRecordBatch)
            await ProcessOutboxRecord(outboxStore, outboxRecord, cancellationToken);
    }

    private async Task ProcessOutboxRecord(IOutboxStore outboxStore, OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(outboxRecord, out var domainEvent))
        {
            await outboxStore.SetFailedAt(outboxRecord, timeProvider.GetUtcNow(), cancellationToken);
            return;
        }

        try
        {
            await DispatchToInnerDispatchers(domainEvent, cancellationToken);
            await outboxStore.SetDispatchedAt(outboxRecord, timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (Exception ex)
        {
            await HandleDispatchFailure(outboxStore, outboxRecord, ex, cancellationToken);
        }
    }

    private bool TryDeserialize(OutboxRecord record, [NotNullWhen(true)] out IDomainEvent? domainEvent)
    {
        try
        {
            domainEvent = domainEventSerializer.Deserialize(record.Payload, record.AssemblyQualifiedType);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Permanent deserialization failure for outbox record {OutboxRecordId}. Record will be marked as failed.", record.Id);
            domainEvent = null;
            return false;
        }
    }

    private async Task DispatchToInnerDispatchers(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dispatchers = scope.ServiceProvider
            .GetKeyedServices<IDispatchDomainEvents>(ServiceKeys.InnerDispatcher)
            .Where(s => s is not OutboxDispatcher);

        foreach (var dispatcher in dispatchers)
            await dispatcher.Dispatch(domainEvent, cancellationToken);
    }

    private async Task HandleDispatchFailure(IOutboxStore outboxStore, OutboxRecord record, Exception ex, CancellationToken cancellationToken)
    {
        if (record.Retries + 1 >= _outboxWorkerOptions.MaxRetries)
        {
            logger.LogError(ex, "Outbox record {OutboxRecordId} failed after {MaxRetries} retries. Record will be marked as failed.",
                record.Id, _outboxWorkerOptions.MaxRetries);
            await outboxStore.SetFailedAt(record, timeProvider.GetUtcNow(), cancellationToken);
        }
        else
        {
            logger.LogWarning(ex, "Transient failure for outbox record {OutboxRecordId}, retry {Retry}/{MaxRetries}.",
                record.Id, record.Retries + 1, _outboxWorkerOptions.MaxRetries);
            await outboxStore.IncrementRetries(record, cancellationToken);
        }
    }
}
