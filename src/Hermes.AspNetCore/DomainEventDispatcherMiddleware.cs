using Hermes.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Hermes.AspNetCore;

internal sealed class DomainEventDispatcherMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next, IEnumerable<IDispatchDomainEvents> dispatchers)
    {
        using var scope = await DomainEventTracker.CreateScope();
        await next(context);

        var domainEvents = scope!.GetAndClearEvents();

        if (domainEvents.Length == 0)
            return;

        foreach (var dispatcher in dispatchers)
            await dispatcher.Dispatch(domainEvents, context.RequestAborted);
    }
}