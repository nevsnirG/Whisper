using Hermes.Outbox.Abstractions;
using MongoDB.Driver;

namespace Hermes.Outbox.MongoDb;
internal sealed class MongoDbOutboxStore(IMongoCollection<OutboxRecord> outboxCollection) : IOutboxStore
{
    public Task Add(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        return outboxCollection.InsertOneAsync(outboxRecord, new InsertOneOptions(), cancellationToken);
    }

    public Task Add(OutboxRecord[] outboxRecords, CancellationToken cancellationToken)
    {
        var options = new InsertManyOptions()
        {
            IsOrdered = false
        };
        return outboxCollection.InsertManyAsync(outboxRecords, options, cancellationToken);
    }

    public async Task<OutboxRecord[]> ReadNextBatch(CancellationToken cancellationToken)
    {
        var sort = Builders<OutboxRecord>.Sort.Ascending(or => or.EnqueuedAtUtc);
        var options = new FindOptions<OutboxRecord, OutboxRecord>()
        {
            AllowDiskUse = true,
            Limit = 10,
            AllowPartialResults = true,
            Sort = sort,
        };
        var cursor = await outboxCollection.FindAsync(or => or.DispatchedAtUtc == null, options, cancellationToken);
        return [.. await cursor.ToListAsync(cancellationToken)];
    }

    public Task Update(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        var filter = Builders<OutboxRecord>.Filter.Eq(or => or.Id, outboxRecord.Id);
        var update = Builders<OutboxRecord>.Update.Set(or => or.DispatchedAtUtc, outboxRecord.DispatchedAtUtc);
        var options = new UpdateOptions { IsUpsert = false };
        return outboxCollection.UpdateOneAsync(filter, update, options, cancellationToken);
    }
}