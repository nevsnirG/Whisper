using Hermes.Abstractions;
using Hermes.MediatR;

namespace Microsoft.Extensions.DependencyInjection;
public static class IHermesBuilderExtensions
{
    public static IHermesBuilder AddMediatR(this IHermesBuilder builder)
    {
        builder.Services
            .AddScoped<IDispatchDomainEvents, MediatorDispatcher>();
        return builder;
    }
}