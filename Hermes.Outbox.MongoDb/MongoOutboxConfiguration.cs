namespace Hermes.Outbox.MongoDb;

public class MongoOutboxConfiguration
{
    public required string ConnectionString { get; init; }
    public required string DatabaseName { get; init; }
    public string CollectionName { get; init; } = "outboxrecords";
}