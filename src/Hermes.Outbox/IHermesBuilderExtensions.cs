using Hermes.Abstractions;
using Hermes.Outbox;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;
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
            .AddSingleton(TimeProvider.System)
            .AddSingleton<OutboxInstallerAwaiter>()
            .AddTransient<IUuidProvider, DefaultUuidProvider>()
            .AddScoped<OutboxDispatcher>()
            .AddScoped<IDispatchDomainEvents>(static sp =>
            {
                var awaiter = sp.GetRequiredService<OutboxInstallerAwaiter>();
                var inner = sp.GetRequiredService<OutboxDispatcher>();

                return awaiter.IsReady
                    ? inner
                    : new BlockingOutboxDispatcher(awaiter, inner);
            })
            .AddScoped<IDomainEventSerializer, DomainEventSerializer>()
            .AddHostedService<OutboxInstaller>()
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