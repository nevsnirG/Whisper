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

        foreach (var dispatcher in dispatchers)
            await dispatcher.Dispatch(domainEvents, context.RequestAborted);
    }
}