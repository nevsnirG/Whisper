using Whisper.AspNetCore;

namespace Microsoft.AspNetCore.Builder;
public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseDomainEventDispatcherMiddleware(this IApplicationBuilder applicationBuilder)
    {
        return applicationBuilder.UseMiddleware<DomainEventDispatcherMiddleware>();
    }
}