using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.UnitTests;

public class OutboxRecordClassMapTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public OutboxRecordClassMapTests()
    {
        // AddMongoDb registers the class map for OutboxRecord.
        new ServiceCollection().AddWhisper(b => b.AddOutbox(o => o.AddMongoDb(new MongoDbOutboxConfiguration
        {
            ConnectionString = "mongodb://fake:27017",
            DatabaseName = "test",
        })));
    }

    // A newer version of the application adds a field to the documents it writes. This version must keep reading
    // them while both run side by side in a rolling deployment, and after a rollback.
    [Fact]
    public void Deserialize_DocumentWithAnElementThisVersionDoesNotKnow_IgnoresIt()
    {
        var document = CreateRecord().ToBsonDocument();
        document["FieldAddedByANewerVersion"] = "some value";

        var record = BsonSerializer.Deserialize<OutboxRecord>(document);

        record.Payload.Should().Be("{}");
    }

    // The first three are documents because that is how they have always been written. A collection holding two
    // representations of one field cannot be filtered or sorted on it.
    [Fact]
    public void Serialize_WritesEveryTimestampInItsEstablishedRepresentation()
    {
        var record = CreateRecord();
        record.DispatchedAtUtc = Timestamp;
        record.FailedAtUtc = Timestamp;
        record.LastErrorAtUtc = Timestamp;
        record.NextRetryAtUtc = Timestamp;

        var document = record.ToBsonDocument();

        document["EnqueuedAtUtc"].BsonType.Should().Be(BsonType.Document);
        document["DispatchedAtUtc"].BsonType.Should().Be(BsonType.Document);
        document["FailedAtUtc"].BsonType.Should().Be(BsonType.Document);
        document["LastErrorAtUtc"].BsonType.Should().Be(BsonType.DateTime);
        document["NextRetryAtUtc"].BsonType.Should().Be(BsonType.DateTime);
        document["EnqueuedAtUtc"].AsBsonDocument.Names.Should().Equal("DateTime", "Ticks", "Offset");
    }

    private static OutboxRecord CreateRecord() => new()
    {
        Id = Guid.NewGuid(),
        EnqueuedAtUtc = Timestamp,
        AssemblyQualifiedType = "Whisper.Test.Event, Whisper.Test",
        Payload = "{}",
    };
}
