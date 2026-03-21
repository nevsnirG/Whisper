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
        var response = await next();

        var domainEvents = Whispers.GetAndClearEvents();

        if (domainEvents is not [])
        {
            foreach (var dispatcher in dispatchers)
                await dispatcher.Dispatch(domainEvents, cancellationToken);
        }

        return response;
    }
}
