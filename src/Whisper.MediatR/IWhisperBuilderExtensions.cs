using MediatR;
using Whisper.Abstractions;
using Whisper.MediatR;

namespace Microsoft.Extensions.DependencyInjection;
public static class IWhisperBuilderExtensions
{
    public static IWhisperBuilder AddMediatR(this IWhisperBuilder builder)
    {
        builder.Services
            .AddScoped<IDispatchDomainEvents, MediatorDispatcher>()
            .AddTransient(typeof(IPipelineBehavior<,>), typeof(DomainEventCaptureBehavior<,>));
        return builder;
    }
}