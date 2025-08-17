using Hermes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Hermes.Outbox;
public static class IHermesBuilderExtensions
{
    public static IHermesBuilder AddOutbox(this IHermesBuilder hermesBuilder, Action<IOutboxBuilder> configure)
    {
        hermesBuilder.Services
            .AddScoped<OutboxDispatcher>()
            .AddSingleton(TimeProvider.System)
            .AddScoped<IDispatchDomainEvents, OutboxDispatcher>()
            .AddScoped<IDomainEventSerializer, DomainEventSerializer>()
            .AddHostedService<OutboxWorker>();
        configure?.Invoke(new OutboxBuilder(hermesBuilder.Services));
        return hermesBuilder;
    }

    private sealed record OutboxBuilder(IServiceCollection Services) : IOutboxBuilder;
}