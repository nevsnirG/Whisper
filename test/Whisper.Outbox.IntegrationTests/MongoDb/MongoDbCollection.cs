namespace Whisper.Outbox.IntegrationTests.MongoDb;

[CollectionDefinition(Name)]
public sealed class MongoDbCollection : ICollectionFixture<MongoDbFixture>
{
    public const string Name = "MongoDb";
}
