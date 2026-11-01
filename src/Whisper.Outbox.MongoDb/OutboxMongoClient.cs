using MongoDB.Driver;

namespace Whisper.Outbox.MongoDb;

/// <summary>
/// The client the outbox stores and installer use, built from <see cref="MongoDbOutboxConfiguration.ConnectionString"/>.
/// Held in its own type so it can never be replaced by, or replace, a <see cref="MongoClient"/> the host registers.
/// </summary>
internal sealed class OutboxMongoClient(MongoClient client)
{
    public MongoClient Client { get; } = client;
}
