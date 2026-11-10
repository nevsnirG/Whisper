using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.MongoDb;

namespace Whisper.Outbox.IntegrationTests.MongoDb;

[Collection(MongoDbCollection.Name)]
public sealed class MongoDbOutboxWorkerLockTests(MongoDbFixture mongoFixture)
{
    // The lock is a lease of 60 seconds that its holder renews every 20 seconds.
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(20);

    private readonly string _databaseName = $"whisper_test_{Guid.NewGuid():N}";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task TryAcquire_WhileAnotherInstanceHoldsTheLock_ReturnsNullUntilItIsReleased()
    {
        await using var instanceA = BuildServiceProvider();
        await using var instanceB = BuildServiceProvider();
        var lockOfA = instanceA.GetRequiredService<IOutboxWorkerLock>();
        var lockOfB = instanceB.GetRequiredService<IOutboxWorkerLock>();

        var heldByA = await lockOfA.TryAcquire(CancellationToken.None);
        var refusedToB = await lockOfB.TryAcquire(CancellationToken.None);
        await heldByA!.DisposeAsync();
        var heldByB = await lockOfB.TryAcquire(CancellationToken.None);

        heldByA.Should().NotBeNull();
        refusedToB.Should().BeNull("instance A holds the lock");
        heldByB.Should().NotBeNull("instance A released the lock");
        await heldByB!.DisposeAsync();
    }

    // The lock is not re-entrant: a second poll of the same instance must not overlap the first either.
    [Fact]
    public async Task TryAcquire_WhileTheSameInstanceHoldsTheLock_ReturnsNull()
    {
        await using var instance = BuildServiceProvider();
        var workerLock = instance.GetRequiredService<IOutboxWorkerLock>();

        await using var held = await workerLock.TryAcquire(CancellationToken.None);
        var second = await workerLock.TryAcquire(CancellationToken.None);

        held.Should().NotBeNull();
        second.Should().BeNull();
    }

    // An instance that crashed never releases its lock and no longer renews it.
    [Fact]
    public async Task TryAcquire_AfterTheHoldersLeaseRanOut_TakesTheLockOver()
    {
        await using var instance = BuildServiceProvider();
        var workerLock = instance.GetRequiredService<IOutboxWorkerLock>();
        await LockCollection().InsertOneAsync(new BsonDocument
        {
            { "_id", "worker" },
            { "Holder", "an instance that crashed" },
            { "ExpiresAtUtc", (_clock.GetUtcNow() + Lease).UtcDateTime },
        });

        var whileTheLeaseRuns = await workerLock.TryAcquire(CancellationToken.None);
        _clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var afterItRanOut = await workerLock.TryAcquire(CancellationToken.None);

        whileTheLeaseRuns.Should().BeNull();
        afterItRanOut.Should().NotBeNull();
        await afterItRanOut!.DisposeAsync();
    }

    // A batch with slow handlers can take longer than the lease.
    [Fact]
    public async Task TryAcquire_WhileTheHolderIsStillWorkingPastItsFirstLease_ReturnsNull()
    {
        await using var instanceA = BuildServiceProvider();
        await using var instanceB = BuildServiceProvider();
        await using var heldByA = await instanceA.GetRequiredService<IOutboxWorkerLock>().TryAcquire(CancellationToken.None);
        var firstExpiry = await ExpiresAtUtc();

        // To one second past the lease the lock was first taken with, in steps that give each renewal that comes
        // due the time to reach the server.
        for (var elapsed = TimeSpan.Zero; elapsed <= Lease; elapsed += TimeSpan.FromSeconds(1))
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }

        var refusedToB = await instanceB.GetRequiredService<IOutboxWorkerLock>().TryAcquire(CancellationToken.None);

        heldByA.Should().NotBeNull();
        (await ExpiresAtUtc()).Should().BeOnOrAfter(firstExpiry + RenewEvery, "the holder renews its lease every 20 seconds");
        refusedToB.Should().BeNull("instance A kept renewing its lease while it held the lock");
    }

    [Fact]
    public async Task Release_RemovesTheLockDocument()
    {
        await using var instance = BuildServiceProvider();
        var held = await instance.GetRequiredService<IOutboxWorkerLock>().TryAcquire(CancellationToken.None);
        var whileHeld = await LockCollection().CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

        await held!.DisposeAsync();

        whileHeld.Should().Be(1);
        (await LockCollection().CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(0);
    }

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddWhisper(b => b.AddOutbox(o => o.AddMongoDb(new MongoDbOutboxConfiguration
        {
            ConnectionString = mongoFixture.ConnectionString,
            DatabaseName = _databaseName,
        })));
        return services.BuildServiceProvider();
    }

    private IMongoCollection<BsonDocument> LockCollection()
        => new MongoClient(mongoFixture.ConnectionString).GetDatabase(_databaseName).GetCollection<BsonDocument>("outboxrecords.locks");

    private async Task<DateTime> ExpiresAtUtc()
        => (await LockCollection().Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["ExpiresAtUtc"].ToUniversalTime();
}
