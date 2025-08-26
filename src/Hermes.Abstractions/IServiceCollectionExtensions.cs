using Hermes.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;
public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddHermes(this IServiceCollection services, Action<IHermesBuilder> configure)
    {
        configure?.Invoke(new HermesBuilder(services));
        return services;
    }

    private sealed record HermesBuilder(IServiceCollection Services) : IHermesBuilder;
}