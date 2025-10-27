﻿namespace Whisper.Outbox.Abstractions;

public interface IOutboxStore
{
    Task Add(OutboxRecord outboxRecord, CancellationToken cancellationToken);

    Task Add(OutboxRecord[] outboxRecords, CancellationToken cancellationToken);

    Task<OutboxRecord[]> ReadNextBatch(CancellationToken cancellationToken);

    Task SetDispatchedAt(OutboxRecord[] outboxRecord, CancellationToken cancellationToken);
}