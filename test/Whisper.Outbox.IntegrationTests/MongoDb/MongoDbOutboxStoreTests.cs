using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.IntegrationTests.Fakes;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.IntegrationTests.MongoDb;

[Collection(MongoDbCollection.Name)]
public sealed class MongoDbOutboxStoreTests : IAsyncLifetime
{
    // Whole milliseconds: BSON DateTime has millisecond precision, so these values round-trip exactly.
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly MongoDbFixture _mongoFixture;
    private ServiceProvider _provider = null!;
    private IMongoCollection<OutboxRecord> _typedCollection = null!;
    private IMongoCollection<BsonDocument> _rawCollection = null!;

    public MongoDbOutboxStoreTests(MongoDbFixture mongoFixture)
    {
        _mongoFixture = mongoFixture;
    }

    public async Task InitializeAsync()
    {
        var databaseName = $"whisper_test_{Guid.NewGuid():N}";
        var configuration = new MongoDbOutboxConfiguration
        {
            ConnectionString = _mongoFixture.ConnectionString,
            DatabaseName = databaseName,
        };

        var services = new ServiceCollection();
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Whispers).Assembly);
        });
        services.AddWhisper(b =>
        {
            b.AddMediatR();
            b.AddOutbox(o => o.AddMongoDb(configuration));
        });
        _provider = services.BuildServiceProvider();

        await _provider.GetRequiredService<IInstallOutbox>().InstallCollection(CancellationToken.None);

        var database = _provider.GetRequiredService<MongoClient>().GetDatabase(databaseName);
        _typedCollection = database.GetCollection<OutboxRecord>(configuration.CollectionName);
        _rawCollection = database.GetCollection<BsonDocument>(configuration.CollectionName);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Add_SingleRecord_RoundTripsThroughReadNextBatch()
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        await store.Add(record, CancellationToken.None);
        var batch = await store.ReadNextBatch(10, BaseTime, CancellationToken.None);

        var stored = batch.Should().ContainSingle().Subject;
        stored.Id.Should().Be(record.Id);
        stored.EnqueuedAtUtc.Should().Be(record.EnqueuedAtUtc);
        stored.Retries.Should().Be(0);
        stored.AssemblyQualifiedType.Should().Be(record.AssemblyQualifiedType);
        stored.Payload.Should().Be(record.Payload);
    }

    [Fact]
    public async Task ScheduleRetry_PersistsRetryAndFailureFields()
    {
        var record = NewRecord();
        var failedAt = BaseTime.AddMinutes(1);
        var nextRetryAt = BaseTime.AddMinutes(5);
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);

        await store.ScheduleRetry(record, new OutboxFailure("System.Exception: transient", failedAt), nextRetryAt, CancellationToken.None);

        var stored = await _typedCollection.Find(x => x.Id == record.Id).SingleAsync();
        stored.Retries.Should().Be(1);
        stored.NextRetryAtUtc.Should().Be(nextRetryAt);
        stored.LastError.Should().Be("System.Exception: transient");
        stored.LastErrorAtUtc.Should().Be(failedAt);
        stored.FailedAtUtc.Should().BeNull();
    }

    // Guards the representation risk: the driver's default DateTimeOffset representation (an array)
    // breaks $lte filters and sorts. The class map must persist these members as BSON DateTime.
    [Fact]
    public async Task ScheduleRetry_PersistsRetryTimestampsAsBsonDateTime()
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);

        await store.ScheduleRetry(record, new OutboxFailure("transient", BaseTime), BaseTime.AddMinutes(5), CancellationToken.None);

        var raw = await _rawCollection
            .Find(new BsonDocument("_id", new BsonBinaryData(record.Id, GuidRepresentation.Standard)))
            .SingleAsync();
        raw["NextRetryAtUtc"].BsonType.Should().Be(BsonType.DateTime);
        raw["LastErrorAtUtc"].BsonType.Should().Be(BsonType.DateTime);
    }

    [Fact]
    public async Task ReadNextBatch_ExcludesNotDue_IncludesDueAndNull_OrderedNullsFirstThenDueTimeThenId()
    {
        var cutoff = BaseTime;
        // Standard-representation GUIDs differing only in the last byte sort in suffix order.
        var nullSecond = NewRecord(id: Guid.Parse("00000000-0000-0000-0000-000000000004"));
        var nullFirst = NewRecord(id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var dueEarlier = NewRecord(id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var dueAtCutoff = NewRecord(id: Guid.Parse("00000000-0000-0000-0000-000000000003"));
        var notDue = NewRecord(id: Guid.Parse("00000000-0000-0000-0000-000000000005"));
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(new[] { nullSecond, nullFirst, dueEarlier, dueAtCutoff, notDue }, CancellationToken.None);
        var failure = new OutboxFailure("transient", cutoff.AddMinutes(-30));
        await store.ScheduleRetry(dueEarlier, failure, cutoff.AddMinutes(-10), CancellationToken.None);
        await store.ScheduleRetry(dueAtCutoff, failure, cutoff, CancellationToken.None);
        await store.ScheduleRetry(notDue, failure, cutoff.AddMilliseconds(1), CancellationToken.None);

        var batch = await store.ReadNextBatch(10, cutoff, CancellationToken.None);

        batch.Select(r => r.Id).Should().Equal(nullFirst.Id, nullSecond.Id, dueEarlier.Id, dueAtCutoff.Id);
    }

    [Fact]
    public async Task ReadNextBatch_DoesNotLoadLastError()
    {
        var record = NewRecord();
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);
        await store.ScheduleRetry(record, new OutboxFailure("transient", BaseTime), null, CancellationToken.None);

        var batch = await store.ReadNextBatch(10, BaseTime, CancellationToken.None);

        // Contract: the hot read path never loads LastError, even though it is persisted.
        batch.Should().ContainSingle()
            .Which.LastError.Should().BeNull();
        (await _typedCollection.Find(x => x.Id == record.Id).SingleAsync()).LastError.Should().Be("transient");
    }

    [Fact]
    public async Task SetFailedAt_PersistsFailureFields_AndExcludesRecordFromNextBatch()
    {
        var record = NewRecord();
        var failedAt = BaseTime.AddMinutes(2);
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(record, CancellationToken.None);

        await store.SetFailedAt(record, new OutboxFailure("System.Exception: it broke", failedAt), CancellationToken.None);

        var batch = await store.ReadNextBatch(10, BaseTime.AddDays(1), CancellationToken.None);
        batch.Should().BeEmpty();
        var stored = await _typedCollection.Find(x => x.Id == record.Id).SingleAsync();
        stored.FailedAtUtc.Should().Be(failedAt);
        stored.LastError.Should().Be("System.Exception: it broke");
        stored.LastErrorAtUtc.Should().Be(failedAt);
    }

    [Fact]
    public async Task ReadNextBatch_RespectsBatchSize()
    {
        using var scope = _provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        await store.Add(new[] { NewRecord(), NewRecord(), NewRecord() }, CancellationToken.None);

        var batch = await store.ReadNextBatch(2, BaseTime, CancellationToken.None);

        batch.Should().HaveCount(2);
    }

    private static OutboxRecord NewRecord(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        EnqueuedAtUtc = BaseTime,
        AssemblyQualifiedType = typeof(TestDomainEvent).AssemblyQualifiedName!,
        Payload = """{"Value":"test"}""",
    };
}
