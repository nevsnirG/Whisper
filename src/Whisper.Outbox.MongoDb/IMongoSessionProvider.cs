using MongoDB.Driver;

namespace Whisper.Outbox.MongoDb;

/// <summary>
/// Supplies the MongoDB session of the unit of work that is active in the current scope, so the events a use case
/// raises are stored together with its own changes. The outbox store uses it when it stores events and at no other
/// time: the worker's reads and updates never use a session.
/// </summary>
public interface IMongoSessionProvider
{
    public bool IsInTransaction => Session is { IsInTransaction: true };

    /// <summary>
    /// The session of the unit of work active in this scope, or null when there is none. With a session, the outbox
    /// store writes in it and never starts, commits, aborts or disposes it. With null, it writes without a session.
    /// </summary>
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