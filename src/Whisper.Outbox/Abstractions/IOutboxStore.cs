namespace Whisper.Outbox.Abstractions;

public interface IOutboxStore
{
    Task Add(OutboxRecord outboxRecord, CancellationToken cancellationToken);

    Task Add(OutboxRecord[] outboxRecords, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the next batch of dispatchable records: not dispatched, not failed and due
    /// (<see cref="OutboxRecord.NextRetryAtUtc"/> is null or at/before <paramref name="dueAtUtc"/>),
    /// ordered by <see cref="OutboxRecord.NextRetryAtUtc"/> ascending with nulls first, then Id ascending.
    /// Implementations do not load <see cref="OutboxRecord.LastError"/> on this hot path.
    /// </summary>
    Task<OutboxRecord[]> ReadNextBatch(int batchSize, DateTimeOffset dueAtUtc, CancellationToken cancellationToken);

    Task SetDispatchedAt(OutboxRecord outboxRecord, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed attempt in a single update: increments <see cref="OutboxRecord.Retries"/> and sets
    /// <see cref="OutboxRecord.NextRetryAtUtc"/> (null = eligible at the next poll),
    /// <see cref="OutboxRecord.LastError"/> and <see cref="OutboxRecord.LastErrorAtUtc"/>.
    /// </summary>
    Task ScheduleRetry(OutboxRecord outboxRecord, OutboxFailure failure, DateTimeOffset? nextRetryAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the record permanently failed: sets <see cref="OutboxRecord.FailedAtUtc"/>,
    /// <see cref="OutboxRecord.LastError"/> and <see cref="OutboxRecord.LastErrorAtUtc"/> from <paramref name="failure"/>.
    /// </summary>
    Task SetFailedAt(OutboxRecord outboxRecord, OutboxFailure failure, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes up to <paramref name="maxRecords"/> dispatched records whose <see cref="OutboxRecord.DispatchedAtUtc"/>
    /// is before <paramref name="dispatchedBeforeUtc"/> and returns how many were deleted. Pending and failed records
    /// are never deleted. Called by the worker when <see cref="OutboxWorkerOptions.RetainDispatchedFor"/> is set.
    /// The default throws <see cref="NotSupportedException"/>, so stores written before this member keep compiling;
    /// the worker then logs a warning once and stops trying.
    /// </summary>
    Task<int> DeleteDispatchedBefore(DateTimeOffset dispatchedBeforeUtc, int maxRecords, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{GetType().Name} does not support deleting dispatched outbox records.");
}
