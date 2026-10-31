using MediatR;
using Whisper.Abstractions;

namespace Whisper.MediatR;

internal sealed class DomainEventCaptureBehavior<TRequest, TResponse>(
    IEnumerable<IDispatchDomainEvents> dispatchers) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        using var _ = Whispers.CreateScope();
        var response = await RequestHandlerDelegateInvoker<TResponse>.Invoke(next, cancellationToken);

        var domainEvents = Whispers.GetAndClearEvents();

        if (domainEvents is not [])
        {
            // Deliberately not cancellable: the handler has run and its changes may already be committed,
            // so cancelling here (a client disconnect, a shutdown) would lose its events. The dispatchers'
            // own timeouts (such as the outbox store's command timeout) bound the wait.
            foreach (var dispatcher in dispatchers)
                await dispatcher.Dispatch(domainEvents, CancellationToken.None);
        }

        return response;
    }
}
