using MongoDB.Driver;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.MongoDb;
internal sealed class MongoDbOutboxInstaller(MongoDbOutboxConfiguration mongoDbOutboxConfiguration, MongoClient mongoClient) : IInstallOutbox
{
    public async Task InstallCollection(CancellationToken cancellationToken)
    {
        var database = mongoClient.GetDatabase(mongoDbOutboxConfiguration.DatabaseName);
        var outboxCollection = database.GetCollection<OutboxRecord>(mongoDbOutboxConfiguration.CollectionName);

        await DropLegacyIndex(outboxCollection, cancellationToken);

        var readyKeys = Builders<OutboxRecord>.IndexKeys
            .Ascending(x => x.NextRetryAtUtc)
            .Ascending(x => x.Id);
        var readyOptions = new CreateIndexOptions<OutboxRecord>
        {
            Name = "ix_outbox_ready_by_due",
            PartialFilterExpression = Builders<OutboxRecord>.Filter.And(
                Builders<OutboxRecord>.Filter.Eq(x => x.DispatchedAtUtc, null),
                Builders<OutboxRecord>.Filter.Eq(x => x.FailedAtUtc, null))
        };

        var failedKeys = Builders<OutboxRecord>.IndexKeys.Descending(x => x.FailedAtUtc);
        var failedOptions = new CreateIndexOptions<OutboxRecord> { Name = "ix_outbox_failed_by_failedat" };

        await outboxCollection.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<OutboxRecord>(readyKeys, readyOptions),
            new CreateIndexModel<OutboxRecord>(failedKeys, failedOptions),
        ], cancellationToken);
    }

    private static async Task DropLegacyIndex(IMongoCollection<OutboxRecord> outboxCollection, CancellationToken cancellationToken)
    {
        try
        {
            await outboxCollection.Indexes.DropOneAsync("ix_outbox_undispatched_by_id", cancellationToken);
        }
        catch (MongoCommandException ex) when (ex.Code is 26 or 27)
        {
            // NamespaceNotFound / IndexNotFound — nothing to drop on fresh installs.
        }
    }
}
