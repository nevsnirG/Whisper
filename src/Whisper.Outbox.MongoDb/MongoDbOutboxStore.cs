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
        var options = new InsertManyOptions()
        {
            IsOrdered = false
        };
        if (mongoSessionProvider.IsInTransaction)
            return outboxCollection.InsertManyAsync(mongoSessionProvider.Session, outboxRecords, options, cancellationToken);
        else
            return outboxCollection.InsertManyAsync(outboxRecords, options, cancellationToken);
    }

    public async Task<OutboxRecord[]> ReadNextBatch(CancellationToken cancellationToken)
    {
        var cursor = await outboxCollection.Find(x => x.DispatchedAtUtc == null)
            .SortBy(x => x.Id)
            .Limit(10)
            .ToListAsync(cancellationToken);

        return [.. cursor];
    }

    public Task SetDispatchedAt(OutboxRecord outboxRecord, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken)
    {
        var filter = Builders<OutboxRecord>.Filter.Eq(or => or.Id, outboxRecord.Id);
        var update = Builders<OutboxRecord>.Update.Set(or => or.DispatchedAtUtc, dispatchedAtUtc);

        if (mongoSessionProvider.IsInTransaction)
            return outboxCollection.UpdateOneAsync(mongoSessionProvider.Session, filter, update, null, cancellationToken);
        else
            return outboxCollection.UpdateOneAsync(filter, update, null, cancellationToken);
    }
}