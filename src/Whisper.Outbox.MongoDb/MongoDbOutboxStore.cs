using MongoDB.Driver;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.MongoDb;

/// <summary>
/// Storing events (<see cref="Add(OutboxRecord, CancellationToken)"/> and its batch overload) is the one thing that
/// belongs to the host's unit of work, so it is the only operation that uses the session of the
/// <see cref="IMongoSessionProvider"/>. Every other operation is the worker's: it runs outside any unit of work
/// and never uses a session.
/// </summary>
internal sealed class MongoDbOutboxStore(IMongoCollection<OutboxRecord> outboxCollection, IMongoSessionProvider? mongoSessionProvider) : IOutboxStore
{
    public Task Add(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        var session = mongoSessionProvider?.Session;
        if (session is not null)
            return outboxCollection.InsertOneAsync(session, outboxRecord, new InsertOneOptions(), cancellationToken);
        else
            return outboxCollection.InsertOneAsync(outboxRecord, new InsertOneOptions(), cancellationToken);
    }

    public Task Add(OutboxRecord[] outboxRecords, CancellationToken cancellationToken)
    {
        var options = new InsertManyOptions { IsOrdered = false };
        var session = mongoSessionProvider?.Session;
        if (session is not null)
            return outboxCollection.InsertManyAsync(session, outboxRecords, options, cancellationToken);
        else
            return outboxCollection.InsertManyAsync(outboxRecords, options, cancellationToken);
    }

    public async Task<OutboxRecord[]> ReadNextBatch(int batchSize, DateTimeOffset dueAtUtc, CancellationToken cancellationToken)
    {
        var filter = Builders<OutboxRecord>.Filter.And(
            Builders<OutboxRecord>.Filter.Eq(x => x.DispatchedAtUtc, null),
            Builders<OutboxRecord>.Filter.Eq(x => x.FailedAtUtc, null),
            Builders<OutboxRecord>.Filter.Or(
                Builders<OutboxRecord>.Filter.Eq(x => x.NextRetryAtUtc, null),
                Builders<OutboxRecord>.Filter.Lte(x => x.NextRetryAtUtc, (DateTimeOffset?)dueAtUtc)));

        var cursor = await outboxCollection.Find(filter)
            .SortBy(x => x.NextRetryAtUtc)
            .ThenBy(x => x.Id)
            .Limit(batchSize)
            .Project<OutboxRecord>(Builders<OutboxRecord>.Projection.Exclude(x => x.LastError))
            .ToListAsync(cancellationToken);

        return [.. cursor];
    }

    public Task SetDispatchedAt(OutboxRecord outboxRecord, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken)
    {
        return UpdateByIdAsync(outboxRecord.Id,
            Builders<OutboxRecord>.Update.Set(or => or.DispatchedAtUtc, dispatchedAtUtc),
            cancellationToken);
    }

    public Task ScheduleRetry(OutboxRecord outboxRecord, OutboxFailure failure, DateTimeOffset? nextRetryAtUtc, CancellationToken cancellationToken)
    {
        return UpdateByIdAsync(outboxRecord.Id,
            Builders<OutboxRecord>.Update
                .Inc(or => or.Retries, 1)
                .Set(or => or.NextRetryAtUtc, nextRetryAtUtc)
                .Set(or => or.LastError, failure.Error)
                .Set(or => or.LastErrorAtUtc, (DateTimeOffset?)failure.OccurredAtUtc),
            cancellationToken);
    }

    public Task SetFailedAt(OutboxRecord outboxRecord, OutboxFailure failure, CancellationToken cancellationToken)
    {
        return UpdateByIdAsync(outboxRecord.Id,
            Builders<OutboxRecord>.Update
                .Set(or => or.FailedAtUtc, (DateTimeOffset?)failure.OccurredAtUtc)
                .Set(or => or.LastError, failure.Error)
                .Set(or => or.LastErrorAtUtc, (DateTimeOffset?)failure.OccurredAtUtc),
            cancellationToken);
    }

    public async Task<int> DeleteDispatchedBefore(DateTimeOffset dispatchedBeforeUtc, int maxRecords, CancellationToken cancellationToken)
    {
        // DeleteMany has no limit, so the ids are selected first to keep each round bounded.
        var filter = Builders<OutboxRecord>.Filter.Lt(x => x.DispatchedAtUtc, (DateTimeOffset?)dispatchedBeforeUtc);
        var ids = await outboxCollection.Find(filter).Limit(maxRecords).Project(x => x.Id).ToListAsync(cancellationToken);
        if (ids.Count == 0)
            return 0;

        var byIds = Builders<OutboxRecord>.Filter.In(x => x.Id, ids);
        var result = await outboxCollection.DeleteManyAsync(byIds, cancellationToken);
        return (int)result.DeletedCount;
    }

    private Task UpdateByIdAsync(Guid id, UpdateDefinition<OutboxRecord> update, CancellationToken cancellationToken)
    {
        var filter = Builders<OutboxRecord>.Filter.Eq(or => or.Id, id);
        return outboxCollection.UpdateOneAsync(filter, update, null, cancellationToken);
    }
}
