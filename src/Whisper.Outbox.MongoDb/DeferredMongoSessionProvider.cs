using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Whisper.Outbox.MongoDb;

/// <summary>
/// Resolves the host's <see cref="IMongoSessionProvider"/> only at the moment events are stored. The outbox worker
/// uses the store too, in scopes without a unit of work, and a provider that cannot even be constructed there (its
/// dependencies belong to a request or a message) must not keep the worker from getting its store.
/// </summary>
internal sealed class DeferredMongoSessionProvider(IServiceProvider scopedServices) : IMongoSessionProvider
{
    public IClientSessionHandle? Session => scopedServices.GetService<IMongoSessionProvider>()?.Session;
}
