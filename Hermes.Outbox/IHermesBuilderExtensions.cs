using Hermes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Hermes.Outbox;
public static class IHermesBuilderExtensions
{
    public static IHermesBuilder AddOutbox(this IHermesBuilder hermesBuilder, Action<IOutboxBuilder> configure)
    {
        hermesBuilder.Services.AddHostedService<OutboxWorker>();
        hermesBuilder.Services.AddScoped<OutboxDispatcher>();
        configure?.Invoke(new OutboxBuilder(hermesBuilder.Services));
        return hermesBuilder;
    }

    private sealed record OutboxBuilder(IServiceCollection Services) : IOutboxBuilder;
}