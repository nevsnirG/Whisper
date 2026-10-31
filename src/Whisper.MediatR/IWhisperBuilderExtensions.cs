using MediatR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whisper.Abstractions;
using Whisper.MediatR;

namespace Microsoft.Extensions.DependencyInjection;
public static class IWhisperBuilderExtensions
{
    public static IWhisperBuilder AddMediatR(this IWhisperBuilder builder)
    {
        // TryAddEnumerable keeps a repeated call from registering a second dispatcher and behavior,
        // which would publish every event twice.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IDispatchDomainEvents, MediatorDispatcher>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(DomainEventCaptureBehavior<,>)));
        return builder;
    }
}