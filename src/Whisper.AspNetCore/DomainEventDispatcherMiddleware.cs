using Whisper.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Whisper.AspNetCore;

internal sealed class DomainEventDispatcherMiddleware(RequestDelegate next, IHostApplicationLifetime? applicationLifetime = null)
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Invoked by AspNetCore")]
    public async Task InvokeAsync(HttpContext context, IEnumerable<IDispatchDomainEvents> dispatchers)
    {
        using var _ = Whispers.CreateScope();
        await next(context);

        var domainEvents = Whispers.GetAndClearEvents();

        if (domainEvents.Length == 0)
            return;

        // Not RequestAborted: the endpoint has run and its changes may already be committed, so a client
        // disconnect must not cancel dispatching its events; only shutdown does.
        var cancellationToken = applicationLifetime?.ApplicationStopping ?? CancellationToken.None;
        foreach (var dispatcher in dispatchers)
            await dispatcher.Dispatch(domainEvents, cancellationToken);
    }
}
