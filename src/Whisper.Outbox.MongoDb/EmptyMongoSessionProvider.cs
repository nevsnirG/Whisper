using MongoDB.Driver;

namespace Whisper.Outbox.MongoDb;
internal sealed class EmptyMongoSessionProvider : IMongoSessionProvider
{
    public IClientSessionHandle? Session => null;
}