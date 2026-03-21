namespace Whisper.Outbox.Abstractions;

public interface IOutboxStore
{
    Task Add(OutboxRecord outboxRecord, CancellationToken cancellationToken);

    Task Add(OutboxRecord[] outboxRecords, CancellationToken cancellationToken);

    Task<OutboxRecord[]> ReadNextBatch(int batchSize, CancellationToken cancellationToken);

    Task SetDispatchedAt(OutboxRecord outboxRecord, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken);

    Task IncrementRetries(OutboxRecord outboxRecord, CancellationToken cancellationToken);

    Task SetFailedAt(OutboxRecord outboxRecord, DateTimeOffset failedAtUtc, CancellationToken cancellationToken);
}