using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.MongoDb;

/// <summary>
/// The worker lock as a lease: one document in a collection next to the outbox, with the moment the lease runs out.
/// The holder renews it while it works and deletes it when it is done. An instance that dies stops renewing, and
/// the others take over once the lease has run out. The lease is timed on the application's clock
/// (<see cref="TimeProvider"/>), so instances whose clocks are further apart than the lease can overlap, which costs
/// duplicate dispatches and nothing else.
/// </summary>
internal sealed class MongoDbOutboxWorkerLock(IMongoCollection<BsonDocument> locks,
                                             TimeProvider timeProvider,
                                             ILogger<MongoDbOutboxWorkerLock> logger) : IOutboxWorkerLock
{
    internal static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(20);

    private const string LockId = "worker";
    private const string Holder = "Holder";
    private const string ExpiresAtUtc = "ExpiresAtUtc";

    private readonly string _instance = $"{Environment.MachineName}/{Guid.NewGuid():N}";

    public async Task<IAsyncDisposable?> TryAcquire(CancellationToken cancellationToken)
    {
        // Every acquisition has its own holder id, so a second poll of this same instance cannot share the lock.
        var holder = $"{_instance}/{Guid.NewGuid():N}";
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The lock is free when its document is gone (never taken, or released) or its lease has run out. Taking it
        // is one atomic upsert: while another holder's lease is running the filter matches nothing, the upsert then
        // tries to insert a second document with the same id, and the duplicate key error means "taken".
        var free = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", LockId),
            Builders<BsonDocument>.Filter.Lte(ExpiresAtUtc, now));
        var take = Builders<BsonDocument>.Update
            .Set(Holder, holder)
            .Set(ExpiresAtUtc, now + Lease);

        try
        {
            await locks.UpdateOneAsync(free, take, new UpdateOptions { IsUpsert = true }, cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return null;
        }

        return new HeldLock(locks, timeProvider, logger, holder);
    }

    private sealed class HeldLock : IAsyncDisposable
    {
        private readonly IMongoCollection<BsonDocument> _locks;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly FilterDefinition<BsonDocument> _mine;
        private readonly CancellationTokenSource _released = new();
        private readonly Task _renewing;

        public HeldLock(IMongoCollection<BsonDocument> locks, TimeProvider timeProvider, ILogger logger, string holder)
        {
            _locks = locks;
            _timeProvider = timeProvider;
            _logger = logger;
            _mine = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", LockId),
                Builders<BsonDocument>.Filter.Eq(Holder, holder));
            _renewing = RenewUntilReleased();
        }

        // A batch can outlast the lease (slow handlers), so the lease is pushed out for as long as the lock is held.
        private async Task RenewUntilReleased()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(RenewEvery, _timeProvider, _released.Token);

                    var expiresAtUtc = _timeProvider.GetUtcNow().UtcDateTime + Lease;
                    var renewed = await _locks.UpdateOneAsync(
                        _mine, Builders<BsonDocument>.Update.Set(ExpiresAtUtc, expiresAtUtc), cancellationToken: _released.Token);

                    if (renewed.MatchedCount == 0)
                    {
                        _logger.LogWarning("The outbox worker lock was taken over by another instance while a batch was still being processed. Records in that batch may be dispatched twice.");
                        return;
                    }
                }
                catch (OperationCanceledException) when (_released.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Renewing the outbox worker lock failed. It is tried again in {RenewEvery}; the lease runs out {Lease} after the last renewal.", RenewEvery, Lease);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _released.Cancel();
            await _renewing;
            _released.Dispose();

            try
            {
                await _locks.DeleteOneAsync(_mine, CancellationToken.None);
            }
            catch (MongoException ex)
            {
                _logger.LogWarning(ex, "Releasing the outbox worker lock failed. It frees itself when its lease runs out, within {Lease}.", Lease);
            }
        }
    }
}
