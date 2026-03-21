using MongoDB.Driver;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.MongoDb;

internal sealed class MongoDbOutboxStore(IMongoCollection<OutboxRecord> outboxCollection, IMongoSessionProvider mongoSessionProvider) : IOutboxStore
{
    public Task Add(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        if (mongoSessionProvider.IsInTransaction)
            return outboxCollection.InsertOneAsync(mongoSessionProvider.Session, outboxRecord, new InsertOneOptions(), cancellationToken);
        else
            return outboxCollection.InsertOneAsync(outboxRecord, new InsertOneOptions(), cancellationToken);
    }

    public Task Add(OutboxRecord[] outboxRecords, CancellationToken cancellationToken)
    {
        var options = new InsertManyOptions { IsOrdered = false };
        if (mongoSessionProvider.IsInTransaction)
            return outboxCollection.InsertManyAsync(mongoSessionProvider.Session, outboxRecords, options, cancellationToken);
        else
            return outboxCollection.InsertManyAsync(outboxRecords, options, cancellationToken);
    }

    public async Task<OutboxRecord[]> ReadNextBatch(int batchSize, CancellationToken cancellationToken)
    {
        var cursor = await outboxCollection.Find(x => x.DispatchedAtUtc == null && x.FailedAtUtc == null)
            .SortBy(x => x.Id)
            .Limit(batchSize)
            .ToListAsync(cancellationToken);

        return [.. cursor];
    }

    public Task SetDispatchedAt(OutboxRecord outboxRecord, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken)
    {
        return UpdateByIdAsync(outboxRecord.Id,
            Builders<OutboxRecord>.Update.Set(or => or.DispatchedAtUtc, dispatchedAtUtc),
            cancellationToken);
    }

    public Task IncrementRetries(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        return UpdateByIdAsync(outboxRecord.Id,
            Builders<OutboxRecord>.Update.Inc(or => or.Retries, 1),
            cancellationToken);
    }

    public Task SetFailedAt(OutboxRecord outboxRecord, DateTimeOffset failedAtUtc, CancellationToken cancellationToken)
    {
        return UpdateByIdAsync(outboxRecord.Id,
            Builders<OutboxRecord>.Update.Set(or => or.FailedAtUtc, failedAtUtc),
            cancellationToken);
    }

    private Task UpdateByIdAsync(Guid id, UpdateDefinition<OutboxRecord> update, CancellationToken cancellationToken)
    {
        var filter = Builders<OutboxRecord>.Filter.Eq(or => or.Id, id);

        if (mongoSessionProvider.IsInTransaction)
            return outboxCollection.UpdateOneAsync(mongoSessionProvider.Session, filter, update, null, cancellationToken);
        else
            return outboxCollection.UpdateOneAsync(filter, update, null, cancellationToken);
    }
}
