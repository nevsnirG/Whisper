namespace Hermes.Outbox.MongoDb;

public sealed class MongoDbOutboxConfiguration
{
    public required string ConnectionString { get; init; }
    public required string DatabaseName { get; init; }
    public string CollectionName { get; init; } = "outboxrecords";
}