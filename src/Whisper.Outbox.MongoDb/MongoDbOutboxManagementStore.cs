using MongoDB.Driver;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.MongoDb;

/// <summary>
/// Singleton management store with zero dependencies on the ambient scoped session provider.
/// Always performs plain, sessionless collection operations, making it immune to host
/// unit-of-work middleware and ambient sessions.
/// </summary>
internal sealed class MongoDbOutboxManagementStore(IMongoCollection<OutboxRecord> outboxCollection) : IOutboxManagementStore
{
    private const int MinPageSize = 1;
    private const int MaxPageSize = 200;

    private static readonly FilterDefinition<OutboxRecord> FailedFilter =
        Builders<OutboxRecord>.Filter.Ne(x => x.FailedAtUtc, null);

    public async Task<OutboxFailedPage> GetFailed(int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);

        var totalCount = await outboxCollection.CountDocumentsAsync(FailedFilter, null, cancellationToken);
        // The excluded Payload hydrates as null despite being non-nullable;
        // the summary mapping below must never read it.
        var records = await outboxCollection.Find(FailedFilter)
            .SortByDescending(x => x.FailedAtUtc)
            .ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .Project<OutboxRecord>(Builders<OutboxRecord>.Projection.Exclude(x => x.Payload))
            .ToListAsync(cancellationToken);

        var summaries = records
            .Select(r => new OutboxRecordSummary(r.Id, r.EnqueuedAtUtc, r.FailedAtUtc!.Value, r.Retries, r.AssemblyQualifiedType, r.LastError, r.LastErrorAtUtc))
            .ToArray();

        return new OutboxFailedPage(summaries, totalCount);
    }

    public async Task<OutboxRecord?> Get(Guid id, CancellationToken cancellationToken)
    {
        return await outboxCollection.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> Retry(Guid id, CancellationToken cancellationToken)
    {
        var filter = Builders<OutboxRecord>.Filter.And(
            Builders<OutboxRecord>.Filter.Eq(x => x.Id, id),
            FailedFilter);

        var result = await outboxCollection.UpdateOneAsync(filter, ResetForRetry(), null, cancellationToken);
        return result.MatchedCount > 0;
    }

    public async Task<long> RetryAll(CancellationToken cancellationToken)
    {
        var result = await outboxCollection.UpdateManyAsync(FailedFilter, ResetForRetry(), null, cancellationToken);
        return result.MatchedCount;
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken)
    {
        var filter = Builders<OutboxRecord>.Filter.And(
            Builders<OutboxRecord>.Filter.Eq(x => x.Id, id),
            FailedFilter);

        var result = await outboxCollection.DeleteOneAsync(filter, cancellationToken);
        return result.DeletedCount > 0;
    }

    private static UpdateDefinition<OutboxRecord> ResetForRetry()
        => Builders<OutboxRecord>.Update
            .Set(x => x.FailedAtUtc, (DateTimeOffset?)null)
            .Set(x => x.Retries, 0)
            .Set(x => x.NextRetryAtUtc, (DateTimeOffset?)null);
}
