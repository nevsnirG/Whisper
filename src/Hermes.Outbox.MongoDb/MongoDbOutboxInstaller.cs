using MongoDB.Driver;

namespace Hermes.Outbox.MongoDb;
internal sealed class MongoDbOutboxInstaller(MongoDbOutboxConfiguration mongoDbOutboxConfiguration, MongoClient mongoClient) : IInstallOutbox
{
    public Task InstallCollection(CancellationToken cancellationToken)
    {
        var database = mongoClient.GetDatabase(mongoDbOutboxConfiguration.DatabaseName);
        var outboxCollection = database.GetCollection<OutboxRecord>(mongoDbOutboxConfiguration.CollectionName);
        var keys = Builders<OutboxRecord>.IndexKeys
            .Ascending(x => x.DispatchedAtUtc)
            .Ascending(x => x.Id);

        var options = new CreateIndexOptions<OutboxRecord>
        {
            Name = "ix_outbox_undispatched_by_id",
            PartialFilterExpression = Builders<OutboxRecord>.Filter.Eq(x => x.DispatchedAtUtc, null)
        };
        return outboxCollection.Indexes.CreateOneAsync(new CreateIndexModel<OutboxRecord>(keys, options), null, cancellationToken);
    }
}