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
                                   IOptions<OutboxRecoverabilityOptions> outboxRecoverabilityOptions,
                                   IServiceScopeFactory serviceScopeFactory,
                                   ILogger<OutboxWorker> logger,
                                   IOutboxWorkerLock? workerLock = null) : BackgroundService
{
    internal const int MaxErrorLength = 32_768;
    internal const int RetentionBatchSize = 1_000;
    // Bounds one cleanup run, so a large backlog is worked off over several runs instead of delaying dispatch.
    internal const int MaxRetentionBatchesPerRun = 10;
    internal static readonly TimeSpan RetentionInterval = TimeSpan.FromMinutes(1);

    private readonly OutboxWorkerOptions _outboxWorkerOptions = outboxWorkerOptions.Value;
    private readonly OutboxRecoverabilityOptions _recoverabilityOptions = outboxRecoverabilityOptions.Value;
    private readonly RecoverabilityPolicy _recoverabilityPolicy = new(outboxRecoverabilityOptions.Value, logger);
    private DateTimeOffset _nextRetentionRunAtUtc = DateTimeOffset.MinValue;
    private bool _retentionUnsupported;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_outboxWorkerOptions.Enabled)
        {
            logger.LogInformation("Outbox worker is disabled on this instance: it stores events but does not dispatch them.");
            return;
        }

        await outboxInstallerAwaiter.WaitForCompletion(stoppingToken);

        logger.LogInformation("Outbox worker started with batch size {BatchSize} and polling interval {PollingIntervalMs}ms.",
            _outboxWorkerOptions.BatchSize, _outboxWorkerOptions.PollingIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            var moreRecordsAreWaiting = false;
            try
            {
                moreRecordsAreWaiting = await Poll(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while processing the outbox batch.");
            }

            await DeleteExpiredDispatchedRecordsWhenDue(stoppingToken);

            // A full batch that dispatched cleanly means a backlog, so the next batch is read right away. After a
            // failure the worker always waits, which keeps retries at least one polling interval apart.
            if (!moreRecordsAreWaiting)
                await Delay(stoppingToken);
        }
    }

    // Returns whether the next poll should follow immediately.
    private async Task<bool> Poll(CancellationToken cancellationToken)
    {
        if (workerLock is null || !_outboxWorkerOptions.ExclusivePolling)
            return await ProcessNextBatch(cancellationToken);

        // Instances that share the outbox take turns: without the lock they would all read the same records and
        // dispatch each of them once per instance.
        await using var held = await workerLock.TryAcquire(cancellationToken);
        if (held is null)
            return false;

        return await ProcessNextBatch(cancellationToken);
    }

    private async Task DeleteExpiredDispatchedRecordsWhenDue(CancellationToken stoppingToken)
    {
        if (_outboxWorkerOptions.RetainDispatchedFor is not { } retention || _retentionUnsupported || stoppingToken.IsCancellationRequested)
            return;

        var now = timeProvider.GetUtcNow();
        if (now < _nextRetentionRunAtUtc)
            return;
        _nextRetentionRunAtUtc = now + RetentionInterval;

        // A retention reaching back past DateTimeOffset.MinValue keeps everything.
        if (retention >= now - DateTimeOffset.MinValue)
            return;
        var dispatchedBeforeUtc = now - retention;

        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            for (var batch = 0; batch < MaxRetentionBatchesPerRun; batch++)
            {
                var deleted = await outboxStore.DeleteDispatchedBefore(dispatchedBeforeUtc, RetentionBatchSize, stoppingToken);
                if (deleted < RetentionBatchSize)
                    break;
            }
        }
        catch (NotSupportedException ex)
        {
            _retentionUnsupported = true;
            logger.LogWarning(ex, "The registered outbox store cannot delete dispatched records, so {Option} is ignored.",
                nameof(OutboxWorkerOptions.RetainDispatchedFor));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting dispatched outbox records older than {DispatchedBeforeUtc} failed; trying again at the next cleanup.",
                dispatchedBeforeUtc);
        }
    }

    private Task Delay(CancellationToken stoppingToken)
    {
        if (_outboxWorkerOptions.UsePollingTimeProvider)
        {
            return Task.Delay(TimeSpan.FromMilliseconds(_outboxWorkerOptions.PollingIntervalMs), timeProvider, stoppingToken)
                .ContinueWith(_ => { }, CancellationToken.None);
        }
        else
        {
            return Task.Delay(_outboxWorkerOptions.PollingIntervalMs, stoppingToken)
                .ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    // Returns whether the batch was full and every record in it was dispatched.
    private async Task<bool> ProcessNextBatch(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var outboxRecordBatch = await outboxStore.ReadNextBatch(_outboxWorkerOptions.BatchSize, timeProvider.GetUtcNow(), cancellationToken);

        var allDispatched = true;
        foreach (var outboxRecord in outboxRecordBatch)
            allDispatched &= await ProcessOutboxRecord(outboxStore, outboxRecord, cancellationToken);

        return allDispatched && outboxRecordBatch.Length >= _outboxWorkerOptions.BatchSize;
    }

    // Returns whether the record was dispatched.
    private async Task<bool> ProcessOutboxRecord(IOutboxStore outboxStore, OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        IDomainEvent domainEvent;
        try
        {
            domainEvent = domainEventSerializer.Deserialize(outboxRecord.Payload, outboxRecord.AssemblyQualifiedType);
        }
        catch (UnknownDomainEventTypeException ex)
        {
            // This build does not know the type, but another version sharing the outbox may (a rolling deployment,
            // a rollback). So it is a failed attempt like any other, not a permanent failure.
            await HandleDispatchFailure(outboxStore, outboxRecord, ex, cancellationToken);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Permanent deserialization failure for outbox record {OutboxRecordId}. Record will be marked as failed.", outboxRecord.Id);
            await outboxStore.SetFailedAt(outboxRecord, CreateFailure(ex), cancellationToken);
            return false;
        }

        try
        {
            await DispatchToInnerDispatchers(domainEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping, which is not a failed attempt: the record stays pending and is dispatched again
            // after the restart.
            throw;
        }
        catch (Exception ex)
        {
            await HandleDispatchFailure(outboxStore, outboxRecord, ex, cancellationToken);
            return false;
        }

        // Outside the try above: the dispatch succeeded, so failing to record it is a storage problem, not a failed
        // attempt. The exception ends the batch (the store is likely unavailable for the next records too) and the
        // record is dispatched again on a later poll. Not cancellable: a shutdown cancelling this mark would also
        // make the record dispatch again after the restart.
        await outboxStore.SetDispatchedAt(outboxRecord, timeProvider.GetUtcNow(), CancellationToken.None);
        return true;
    }

    private async Task DispatchToInnerDispatchers(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var dispatchers = scope.ServiceProvider
            .GetKeyedServices<IDispatchDomainEvents>(ServiceKeys.InnerDispatcher)
            .Where(s => s is not OutboxDispatcher);

        // No use case is running here, so an event a handler raises has no scope to be collected in and would be
        // lost. Whispers.About throws instead; a handler that sends a command gets a scope from that command.
        using var dispatching = Whispers.BeginDispatch();

        foreach (var dispatcher in dispatchers)
            await dispatcher.Dispatch(domainEvent, cancellationToken);
    }

    private async Task HandleDispatchFailure(IOutboxStore outboxStore, OutboxRecord record, Exception ex, CancellationToken cancellationToken)
    {
        var failure = CreateFailure(ex);

        if (_recoverabilityPolicy.IsUnrecoverable(ex))
        {
            logger.LogError(ex, "Unrecoverable failure for outbox record {OutboxRecordId}. Record will be marked as failed.", record.Id);
            await outboxStore.SetFailedAt(record, failure, cancellationToken);
            return;
        }

        if (record.Retries + 1 >= _recoverabilityOptions.MaxRetries)
        {
            logger.LogError(ex, "Outbox record {OutboxRecordId} failed after {MaxRetries} retries. Record will be marked as failed.",
                record.Id, _recoverabilityOptions.MaxRetries);
            await outboxStore.SetFailedAt(record, failure, cancellationToken);
            return;
        }

        var nextRetryAtUtc = _recoverabilityPolicy.NextRetryAt(record.Retries + 1, failure.OccurredAtUtc);
        if (nextRetryAtUtc is null)
            logger.LogWarning(ex, "Transient failure for outbox record {OutboxRecordId}, retry {Retry}/{MaxRetries}, next attempt at the next poll.",
                record.Id, record.Retries + 1, _recoverabilityOptions.MaxRetries);
        else
            logger.LogWarning(ex, "Transient failure for outbox record {OutboxRecordId}, retry {Retry}/{MaxRetries}, next attempt due at {NextRetryAtUtc}.",
                record.Id, record.Retries + 1, _recoverabilityOptions.MaxRetries, nextRetryAtUtc);
        await outboxStore.ScheduleRetry(record, failure, nextRetryAtUtc, cancellationToken);
    }

    private OutboxFailure CreateFailure(Exception exception)
        => new(Truncate(exception.ToString()), timeProvider.GetUtcNow());

    private static string Truncate(string error)
    {
        if (error.Length <= MaxErrorLength)
            return error;

        var length = char.IsHighSurrogate(error[MaxErrorLength - 1]) ? MaxErrorLength - 1 : MaxErrorLength;
        return error[..length];
    }
}
