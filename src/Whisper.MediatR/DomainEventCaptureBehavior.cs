using MediatR;
using Microsoft.Extensions.Hosting;
using Whisper.Abstractions;

namespace Whisper.MediatR;

internal sealed class DomainEventCaptureBehavior<TRequest, TResponse>(
    IEnumerable<IDispatchDomainEvents> dispatchers,
    IHostApplicationLifetime? applicationLifetime = null) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        using var _ = Whispers.CreateScope();
        var response = await RequestHandlerDelegateInvoker<TResponse>.Invoke(next, cancellationToken);

        var domainEvents = Whispers.GetAndClearEvents();

        if (domainEvents is not [])
        {
            // The handler has run and its changes may already be committed, so a cancelled request
            // (e.g. a client disconnect) must not cancel dispatching its events; only shutdown does.
            var dispatchCancellationToken = applicationLifetime?.ApplicationStopping ?? CancellationToken.None;
            foreach (var dispatcher in dispatchers)
                await dispatcher.Dispatch(domainEvents, dispatchCancellationToken);
        }

        return response;
    }
}
