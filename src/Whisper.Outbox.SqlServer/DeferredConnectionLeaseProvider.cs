using Microsoft.Extensions.DependencyInjection;

namespace Whisper.Outbox.SqlServer;

/// <summary>
/// Resolves the host's <see cref="IConnectionLeaseProvider"/> only at the moment events are stored. The outbox worker
/// uses the store too, in scopes without a unit of work, and a provider that cannot even be constructed there (its
/// dependencies belong to a request or a message) must not keep the worker from getting its store.
/// </summary>
internal sealed class DeferredConnectionLeaseProvider(IServiceProvider scopedServices) : IConnectionLeaseProvider
{
    public ValueTask<ConnectionLease?> Provide(CancellationToken cancellationToken)
    {
        return scopedServices.GetService<IConnectionLeaseProvider>() is { } provider
            ? provider.Provide(cancellationToken)
            : ValueTask.FromResult<ConnectionLease?>(null);
    }
}
