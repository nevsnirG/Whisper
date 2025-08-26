using Hermes.Outbox.Abstractions;
using MongoDB.Driver;

namespace Hermes.Outbox.MongoDb;
internal sealed class MongoDbOutboxStore(IMongoCollection<OutboxRecord> outboxCollection, IMongoSessionProvider mongoSessionProvider) : IOutboxStore
{
    public Task Add(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        if (mongoSessionProvider.Session is not null)
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
        if (mongoSessionProvider.Session is not null)
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

    public Task SetDispatchedAt(OutboxRecord[] outboxRecordBatch, CancellationToken cancellationToken)
    {
        var updateOneModels = new List<UpdateOneModel<OutboxRecord>>(outboxRecordBatch.Length);

        foreach (var outboxRecord in outboxRecordBatch)
        {
            var filter = Builders<OutboxRecord>.Filter.Eq(or => or.Id, outboxRecord.Id);
            var update = Builders<OutboxRecord>.Update.Set(or => or.DispatchedAtUtc, outboxRecord.DispatchedAtUtc);
            updateOneModels.Add(new UpdateOneModel<OutboxRecord>(filter, update));
        }

        var bulkWriteOptions = new BulkWriteOptions
        {
            IsOrdered = false
        };

        if (mongoSessionProvider.Session is not null)
            return outboxCollection.BulkWriteAsync(mongoSessionProvider.Session, updateOneModels, bulkWriteOptions, cancellationToken);
        else
            return outboxCollection.BulkWriteAsync(updateOneModels, bulkWriteOptions, cancellationToken);
    }
}