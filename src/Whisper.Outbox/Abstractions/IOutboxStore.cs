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
}
