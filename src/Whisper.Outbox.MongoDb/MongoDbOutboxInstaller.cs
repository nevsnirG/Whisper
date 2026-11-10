using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.MongoDb;
internal sealed class MongoDbOutboxInstaller(MongoDbOutboxConfiguration mongoDbOutboxConfiguration,
                                            OutboxMongoClient outboxMongoClient,
                                            IOptions<OutboxWorkerOptions> outboxWorkerOptions) : IInstallOutbox
{
    public async Task InstallCollection(CancellationToken cancellationToken)
    {
        var database = outboxMongoClient.Client.GetDatabase(mongoDbOutboxConfiguration.DatabaseName);
        var outboxCollection = database.GetCollection<OutboxRecord>(mongoDbOutboxConfiguration.CollectionName);

        await DropLegacyIndex(outboxCollection, "ix_outbox_undispatched_by_id", cancellationToken);
        // Replaced by ix_outbox_failed_by_failedat_id below. Its key lacked _id, the second half of the order failed
        // records are listed in, so every page of that list was sorted in memory out of all failed records.
        await DropLegacyIndex(outboxCollection, "ix_outbox_failed_by_failedat", cancellationToken);

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

        var failedKeys = Builders<OutboxRecord>.IndexKeys
            .Descending(x => x.FailedAtUtc)
            .Ascending(x => x.Id);
        var failedOptions = new CreateIndexOptions<OutboxRecord> { Name = "ix_outbox_failed_by_failedat_id" };

        List<CreateIndexModel<OutboxRecord>> indexes =
        [
            new(readyKeys, readyOptions),
            new(failedKeys, failedOptions),
        ];

        // Only with retention switched on: the index gets an entry for every record, which is wasted work when
        // nothing ever deletes them. Without it the cleanup reads the whole collection to find what expired.
        if (outboxWorkerOptions.Value.RetainDispatchedFor is not null)
        {
            indexes.Add(new(
                Builders<OutboxRecord>.IndexKeys.Ascending(x => x.DispatchedAtUtc),
                new CreateIndexOptions<OutboxRecord> { Name = "ix_outbox_dispatched_by_dispatchedat" }));
        }

        await outboxCollection.Indexes.CreateManyAsync(indexes, cancellationToken);
    }

    private static async Task DropLegacyIndex(IMongoCollection<OutboxRecord> outboxCollection, string name, CancellationToken cancellationToken)
    {
        try
        {
            await outboxCollection.Indexes.DropOneAsync(name, cancellationToken);
        }
        catch (MongoCommandException ex) when (ex.Code is 26 or 27)
        {
            // NamespaceNotFound / IndexNotFound — nothing to drop on fresh installs.
        }
    }
}
