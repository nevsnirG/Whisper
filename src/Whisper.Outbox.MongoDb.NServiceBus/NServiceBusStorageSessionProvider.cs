using MongoDB.Driver;
using NServiceBus.Storage.MongoDB;

namespace Whisper.Outbox.MongoDb.NServiceBus;

internal sealed class NServiceBusStorageSessionProvider : IMongoSessionProvider
{
    private readonly IMongoSynchronizedStorageSession _sharedSession;

    public NServiceBusStorageSessionProvider(IMongoSynchronizedStorageSession sharedSession)
    {
        _sharedSession = sharedSession;
    }

    public IClientSessionHandle? Session => _sharedSession.MongoSession;
}