using Hermes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Hermes.MediatR;
public static class IHermesBuilderExtensions
{
    public static IHermesBuilder AddMediatR(this IHermesBuilder builder)
    {
        builder.Services
            .AddScoped<IDispatchDomainEvents, MediatorDispatcher>()
            .AddMediatR(c => c.RegisterServicesFromAssemblyContaining<MediatorDispatcher>());
        return builder;
    }
}