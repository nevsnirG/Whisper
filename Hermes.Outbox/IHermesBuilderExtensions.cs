using Hermes.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hermes.Outbox;
public static class IHermesBuilderExtensions
{
    internal const string ServiceKey = "innerDispatcher";

    public static IHermesBuilder AddOutbox(this IHermesBuilder hermesBuilder, Action<IOutboxBuilder> configure)
    {
        var domainEventDispatcherServiceDescriptors = hermesBuilder.Services
            .Where(s => s.ServiceType == typeof(IDispatchDomainEvents))
            .ToArray();
        hermesBuilder.Services.RemoveAll<IDispatchDomainEvents>();

        foreach (var serviceDescriptor in domainEventDispatcherServiceDescriptors)
        {
            AddKeyedFromDescriptor(hermesBuilder.Services, serviceDescriptor, ServiceKey);
        }
        hermesBuilder.Services
            .AddScoped<OutboxDispatcher>()
            .AddSingleton(TimeProvider.System)
            .AddScoped<IDispatchDomainEvents, OutboxDispatcher>()
            .AddScoped<IDomainEventSerializer, DomainEventSerializer>()
            .AddHostedService<OutboxWorker>();
        configure?.Invoke(new OutboxBuilder(hermesBuilder.Services));
        return hermesBuilder;
    }

    private static void AddKeyedFromDescriptor(IServiceCollection services, ServiceDescriptor sd, object serviceKey)
    {
        if (sd.ImplementationType is not null)
        {
            services.Add(new(sd.ServiceType, serviceKey, sd.ImplementationType, sd.Lifetime));
        }
        else if (sd.ImplementationFactory is not null)
        {
            services.Add(new(sd.ServiceType, serviceKey, (sp, key) => sd.ImplementationFactory(sp), sd.Lifetime));
        }
        else
        {
            services.Add(new(sd.ServiceType, serviceKey, sd.ImplementationInstance!));
        }
    }

    private sealed record OutboxBuilder(IServiceCollection Services) : IOutboxBuilder;
}