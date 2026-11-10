using Whisper.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Whisper.AspNetCore;

internal sealed class DomainEventDispatcherMiddleware(RequestDelegate next)
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Invoked by AspNetCore")]
    public async Task InvokeAsync(HttpContext context, IEnumerable<IDispatchDomainEvents> dispatchers)
    {
        using var _ = Whispers.CreateScope();
        await next(context);

        var domainEvents = Whispers.GetAndClearEvents();

        if (domainEvents.Length == 0)
            return;

        // Events are dispatched once, after the use case that raised them. A handler that raises another one
        // while it handles these gets an exception instead of having it dropped when this scope ends.
        using var dispatching = Whispers.BeginDispatch();

        // Deliberately not cancellable (not RequestAborted): the endpoint has run and its changes may already
        // be committed, so cancelling here (a client disconnect, a shutdown) would lose its events. The
        // dispatchers' own timeouts (such as the outbox store's command timeout) bound the wait.
        foreach (var dispatcher in dispatchers)
            await dispatcher.Dispatch(domainEvents, CancellationToken.None);
    }
}
