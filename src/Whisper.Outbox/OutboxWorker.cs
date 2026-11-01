using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox;

internal sealed class OutboxWorker(IDomainEventSerializer domainEventSerializer,
                                   TimeProvider timeProvider,
                                   OutboxInstallerAwaiter outboxInstallerAwaiter,
                                   IOptions<OutboxWorkerOptions> outboxWorkerOptions,
                                   IOptions<OutboxRecoverabilityOptions> outboxRecoverabilityOptions,
                                   IServiceScopeFactory serviceScopeFactory,
                                   ILogger<OutboxWorker> logger) : BackgroundService
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

            await DeleteExpiredDispatchedRecordsWhenDue(stoppingToken);
            await Delay(stoppingToken);
        }
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

    private async Task ProcessNextBatch(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var outboxRecordBatch = await outboxStore.ReadNextBatch(_outboxWorkerOptions.BatchSize, timeProvider.GetUtcNow(), cancellationToken);

        if (outboxRecordBatch is [])
            return;

        foreach (var outboxRecord in outboxRecordBatch)
            await ProcessOutboxRecord(outboxStore, outboxRecord, cancellationToken);
    }

    private async Task ProcessOutboxRecord(IOutboxStore outboxStore, OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        if (!TryDeserialize(outboxRecord, out var domainEvent, out var deserializationError))
        {
            await outboxStore.SetFailedAt(outboxRecord, CreateFailure(deserializationError), cancellationToken);
            return;
        }

        try
        {
            await DispatchToInnerDispatchers(domainEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            await HandleDispatchFailure(outboxStore, outboxRecord, ex, cancellationToken);
            return;
        }

        // Outside the try above: the dispatch succeeded, so failing to record it is a storage problem, not a failed
        // attempt. The exception ends the batch (the store is likely unavailable for the next records too) and the
        // record is dispatched again on a later poll. Not cancellable: a shutdown cancelling this mark would also
        // make the record dispatch again after the restart.
        await outboxStore.SetDispatchedAt(outboxRecord, timeProvider.GetUtcNow(), CancellationToken.None);
    }

    private bool TryDeserialize(OutboxRecord record, [NotNullWhen(true)] out IDomainEvent? domainEvent, [NotNullWhen(false)] out Exception? error)
    {
        try
        {
            domainEvent = domainEventSerializer.Deserialize(record.Payload, record.AssemblyQualifiedType);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Permanent deserialization failure for outbox record {OutboxRecordId}. Record will be marked as failed.", record.Id);
            domainEvent = null;
            error = ex;
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
