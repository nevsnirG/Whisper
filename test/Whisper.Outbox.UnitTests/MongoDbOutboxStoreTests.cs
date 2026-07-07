using MongoDB.Driver;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.UnitTests;

public sealed class MongoDbOutboxStoreTests : IDisposable
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IMongoCollection<OutboxRecord> _collection = Substitute.For<IMongoCollection<OutboxRecord>>();
    private readonly CancellationTokenSource _cts = new();

    public void Dispose() => _cts.Dispose();

    [Fact]
    public async Task Add_WithoutSessionProvider_InsertsWithoutSession()
    {
        var record = CreateRecord();
        var sut = new MongoDbOutboxStore(_collection, null);

        await sut.Add(record, _cts.Token);

        await _collection.Received(1).InsertOneAsync(Arg.Is(record), Arg.Any<InsertOneOptions>(), Arg.Is(_cts.Token));
        await _collection.DidNotReceive().InsertOneAsync(Arg.Any<IClientSessionHandle>(), Arg.Any<OutboxRecord>(), Arg.Any<InsertOneOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_WithActiveSession_InsertsWithThatSession()
    {
        var record = CreateRecord();
        var session = Substitute.For<IClientSessionHandle>();
        var sut = new MongoDbOutboxStore(_collection, ProviderWithSession(session));

        await sut.Add(record, _cts.Token);

        await _collection.Received(1).InsertOneAsync(Arg.Is(session), Arg.Is(record), Arg.Any<InsertOneOptions>(), Arg.Is(_cts.Token));
        await _collection.DidNotReceive().InsertOneAsync(Arg.Any<OutboxRecord>(), Arg.Any<InsertOneOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_WithProviderWithoutActiveSession_InsertsWithoutSession()
    {
        var record = CreateRecord();
        var sut = new MongoDbOutboxStore(_collection, ProviderWithSession(null));

        await sut.Add(record, _cts.Token);

        await _collection.Received(1).InsertOneAsync(Arg.Is(record), Arg.Any<InsertOneOptions>(), Arg.Is(_cts.Token));
        await _collection.DidNotReceive().InsertOneAsync(Arg.Any<IClientSessionHandle>(), Arg.Any<OutboxRecord>(), Arg.Any<InsertOneOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddBatch_WithoutSessionProvider_InsertsManyUnorderedWithoutSession()
    {
        var records = new[] { CreateRecord(), CreateRecord() };
        var sut = new MongoDbOutboxStore(_collection, null);

        await sut.Add(records, _cts.Token);

        await _collection.Received(1).InsertManyAsync(
            Arg.Is<IEnumerable<OutboxRecord>>(d => ReferenceEquals(d, records)),
            Arg.Is<InsertManyOptions>(o => !o.IsOrdered),
            Arg.Is(_cts.Token));
        await _collection.DidNotReceive().InsertManyAsync(Arg.Any<IClientSessionHandle>(), Arg.Any<IEnumerable<OutboxRecord>>(), Arg.Any<InsertManyOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddBatch_WithActiveSession_InsertsManyWithThatSession()
    {
        var records = new[] { CreateRecord(), CreateRecord() };
        var session = Substitute.For<IClientSessionHandle>();
        var sut = new MongoDbOutboxStore(_collection, ProviderWithSession(session));

        await sut.Add(records, _cts.Token);

        await _collection.Received(1).InsertManyAsync(
            Arg.Is(session),
            Arg.Is<IEnumerable<OutboxRecord>>(d => ReferenceEquals(d, records)),
            Arg.Is<InsertManyOptions>(o => !o.IsOrdered),
            Arg.Is(_cts.Token));
        await _collection.DidNotReceive().InsertManyAsync(Arg.Any<IEnumerable<OutboxRecord>>(), Arg.Any<InsertManyOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDispatchedAt_WithoutSessionProvider_UpdatesWithoutSession()
    {
        var record = CreateRecord();
        var sut = new MongoDbOutboxStore(_collection, null);

        await sut.SetDispatchedAt(record, Timestamp, _cts.Token);

        await _collection.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<OutboxRecord>>(),
            Arg.Any<UpdateDefinition<OutboxRecord>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Is(_cts.Token));
        await _collection.DidNotReceive().UpdateOneAsync(Arg.Any<IClientSessionHandle>(), Arg.Any<FilterDefinition<OutboxRecord>>(), Arg.Any<UpdateDefinition<OutboxRecord>>(), Arg.Any<UpdateOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDispatchedAt_WithActiveSession_UpdatesWithThatSession()
    {
        var record = CreateRecord();
        var session = Substitute.For<IClientSessionHandle>();
        var sut = new MongoDbOutboxStore(_collection, ProviderWithSession(session));

        await sut.SetDispatchedAt(record, Timestamp, _cts.Token);

        await _collection.Received(1).UpdateOneAsync(
            Arg.Is(session),
            Arg.Any<FilterDefinition<OutboxRecord>>(),
            Arg.Any<UpdateDefinition<OutboxRecord>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Is(_cts.Token));
        await _collection.DidNotReceive().UpdateOneAsync(Arg.Any<FilterDefinition<OutboxRecord>>(), Arg.Any<UpdateDefinition<OutboxRecord>>(), Arg.Any<UpdateOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleRetry_WithoutSessionProvider_UpdatesWithoutSession()
    {
        var record = CreateRecord();
        var sut = new MongoDbOutboxStore(_collection, null);

        await sut.ScheduleRetry(record, CreateFailure(), Timestamp.AddMinutes(5), _cts.Token);

        await _collection.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<OutboxRecord>>(),
            Arg.Any<UpdateDefinition<OutboxRecord>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Is(_cts.Token));
        await _collection.DidNotReceive().UpdateOneAsync(Arg.Any<IClientSessionHandle>(), Arg.Any<FilterDefinition<OutboxRecord>>(), Arg.Any<UpdateDefinition<OutboxRecord>>(), Arg.Any<UpdateOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleRetry_WithActiveSession_UpdatesWithThatSession()
    {
        var record = CreateRecord();
        var session = Substitute.For<IClientSessionHandle>();
        var sut = new MongoDbOutboxStore(_collection, ProviderWithSession(session));

        await sut.ScheduleRetry(record, CreateFailure(), null, _cts.Token);

        await _collection.Received(1).UpdateOneAsync(
            Arg.Is(session),
            Arg.Any<FilterDefinition<OutboxRecord>>(),
            Arg.Any<UpdateDefinition<OutboxRecord>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Is(_cts.Token));
        await _collection.DidNotReceive().UpdateOneAsync(Arg.Any<FilterDefinition<OutboxRecord>>(), Arg.Any<UpdateDefinition<OutboxRecord>>(), Arg.Any<UpdateOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetFailedAt_WithoutSessionProvider_UpdatesWithoutSession()
    {
        var record = CreateRecord();
        var sut = new MongoDbOutboxStore(_collection, null);

        await sut.SetFailedAt(record, CreateFailure(), _cts.Token);

        await _collection.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<OutboxRecord>>(),
            Arg.Any<UpdateDefinition<OutboxRecord>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Is(_cts.Token));
        await _collection.DidNotReceive().UpdateOneAsync(Arg.Any<IClientSessionHandle>(), Arg.Any<FilterDefinition<OutboxRecord>>(), Arg.Any<UpdateDefinition<OutboxRecord>>(), Arg.Any<UpdateOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetFailedAt_WithActiveSession_UpdatesWithThatSession()
    {
        var record = CreateRecord();
        var session = Substitute.For<IClientSessionHandle>();
        var sut = new MongoDbOutboxStore(_collection, ProviderWithSession(session));

        await sut.SetFailedAt(record, CreateFailure(), _cts.Token);

        await _collection.Received(1).UpdateOneAsync(
            Arg.Is(session),
            Arg.Any<FilterDefinition<OutboxRecord>>(),
            Arg.Any<UpdateDefinition<OutboxRecord>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Is(_cts.Token));
        await _collection.DidNotReceive().UpdateOneAsync(Arg.Any<FilterDefinition<OutboxRecord>>(), Arg.Any<UpdateDefinition<OutboxRecord>>(), Arg.Any<UpdateOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadNextBatch_WithoutSessionProvider_ReturnsRecordsFromCursor()
    {
        var record = CreateRecord();
        var cursor = Substitute.For<IAsyncCursor<OutboxRecord>>();
        cursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(true, false);
        cursor.Current.Returns(new[] { record });
        _collection.FindAsync(Arg.Any<FilterDefinition<OutboxRecord>>(), Arg.Any<FindOptions<OutboxRecord, OutboxRecord>>(), Arg.Any<CancellationToken>())
            .Returns(cursor);
        var sut = new MongoDbOutboxStore(_collection, null);

        var result = await sut.ReadNextBatch(10, Timestamp, _cts.Token);

        result.Should().ContainSingle().Which.Should().BeSameAs(record);
    }

    [Fact]
    public async Task ReadNextBatch_NoPendingRecords_ReturnsEmptyArray()
    {
        var cursor = Substitute.For<IAsyncCursor<OutboxRecord>>();
        cursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(false);
        _collection.FindAsync(Arg.Any<FilterDefinition<OutboxRecord>>(), Arg.Any<FindOptions<OutboxRecord, OutboxRecord>>(), Arg.Any<CancellationToken>())
            .Returns(cursor);
        var sut = new MongoDbOutboxStore(_collection, null);

        var result = await sut.ReadNextBatch(10, Timestamp, _cts.Token);

        result.Should().BeEmpty();
    }

    private static IMongoSessionProvider ProviderWithSession(IClientSessionHandle? session)
    {
        var provider = Substitute.For<IMongoSessionProvider>();
        provider.Session.Returns(session);
        return provider;
    }

    private static OutboxRecord CreateRecord() => new()
    {
        Id = Guid.NewGuid(),
        EnqueuedAtUtc = Timestamp,
        AssemblyQualifiedType = "Whisper.Test.Event, Whisper.Test",
        Payload = "{}",
    };

    private static OutboxFailure CreateFailure() => new("Simulated failure", Timestamp);
}
