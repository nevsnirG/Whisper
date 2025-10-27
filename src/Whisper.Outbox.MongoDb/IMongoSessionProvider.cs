using MongoDB.Driver;

namespace Whisper.Outbox.MongoDb;
public interface IMongoSessionProvider
{
    IClientSessionHandle? Session { get; }
}

public interface IMongoSessionProviderInitializer
{
    void Initialize(IClientSessionHandle session);
}

internal sealed class MongoSessionProvider : IMongoSessionProvider, IMongoSessionProviderInitializer
{
    public IClientSessionHandle? Session { get; private set; }

    public void Initialize(IClientSessionHandle session)
    {
        Session = session;
    }
}