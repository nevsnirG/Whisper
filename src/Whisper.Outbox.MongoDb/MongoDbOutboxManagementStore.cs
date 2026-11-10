using MongoDB.Bson;
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

    // The order of ix_outbox_failed_by_failedat_id, so a page is read straight from the index.
    private static readonly SortDefinition<OutboxRecord> NewestFailureFirst =
        Builders<OutboxRecord>.Sort.Descending(x => x.FailedAtUtc).Ascending(x => x.Id);

    // What a summary needs and no more: no payload, and of the error only its start, cut on the server. (The server
    // cuts it in a $project stage because a find projection can only compute fields from MongoDB 4.4 on.)
    private static readonly BsonDocument SummaryFields = new()
    {
        { nameof(OutboxRecord.EnqueuedAtUtc), 1 },
        { nameof(OutboxRecord.FailedAtUtc), 1 },
        { nameof(OutboxRecord.Retries), 1 },
        { nameof(OutboxRecord.AssemblyQualifiedType), 1 },
        { nameof(OutboxRecord.LastErrorAtUtc), 1 },
        {
            nameof(OutboxRecord.LastError), new BsonDocument("$substrCP", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$" + nameof(OutboxRecord.LastError), string.Empty }),
                0,
                OutboxRecordSummary.LastErrorPreviewLength,
            })
        },
    };

    public async Task<OutboxFailedPage> GetFailed(int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
        // 64-bit math: (page - 1) * pageSize overflows int for huge pages; any offset that large is past the end anyway.
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

        var totalCount = await outboxCollection.CountDocumentsAsync(FailedFilter, null, cancellationToken);
        // Members left out of the projection (Payload among them) hydrate as null or default even where the
        // type says non-nullable; the summary mapping below must never read them.
        var records = await outboxCollection.Aggregate()
            .Match(FailedFilter)
            .Sort(NewestFailureFirst)
            .Skip(skip)
            .Limit(pageSize)
            .Project<OutboxRecord>(SummaryFields)
            .ToListAsync(cancellationToken);

        var summaries = records
            .Select(r => new OutboxRecordSummary(
                r.Id,
                r.EnqueuedAtUtc,
                r.FailedAtUtc!.Value,
                r.Retries,
                r.AssemblyQualifiedType,
                string.IsNullOrEmpty(r.LastError) ? null : r.LastError,
                r.LastErrorAtUtc))
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
