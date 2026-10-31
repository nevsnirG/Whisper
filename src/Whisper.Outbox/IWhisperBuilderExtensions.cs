using Microsoft.Extensions.DependencyInjection.Extensions;
using Whisper.Abstractions;
using Whisper.Outbox;
using Whisper.Outbox.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;

public static class IWhisperBuilderExtensions
{
    public static IWhisperBuilder AddOutbox(this IWhisperBuilder whisperBuilder, Action<IOutboxBuilder> configure)
    {
        DecorateDispatchersAsInnerDispatchers(whisperBuilder);

        whisperBuilder.Services.TryAddTransient<IUuidProvider, DefaultUuidProvider>();
        whisperBuilder.Services.TryAddSingleton(TimeProvider.System);
        whisperBuilder.Services
            .AddSingleton<OutboxInstallerAwaiter>()
            .AddScoped<OutboxDispatcher>()
            .AddScoped<IDispatchDomainEvents>(static sp =>
            {
                var awaiter = sp.GetRequiredService<OutboxInstallerAwaiter>();
                var inner = sp.GetRequiredService<OutboxDispatcher>();

                return awaiter.IsReady
                    ? inner
                    : new BlockingOutboxDispatcher(awaiter, inner);
            })
            .AddSingleton<IDomainEventSerializer, DomainEventSerializer>()
            .AddHostedService<OutboxInstaller>()
            .AddHostedService<OutboxWorker>();
        // BatchSize 0 means "no limit" to the Mongo driver and PollingIntervalMs -1 is Timeout.Infinite to Task.Delay.
        whisperBuilder.Services.AddOptions<OutboxWorkerOptions>()
            .Validate(o => o.BatchSize > 0, $"{nameof(OutboxWorkerOptions)}.{nameof(OutboxWorkerOptions.BatchSize)} must be greater than 0.")
            .Validate(o => o.PollingIntervalMs > 0, $"{nameof(OutboxWorkerOptions)}.{nameof(OutboxWorkerOptions.PollingIntervalMs)} must be greater than 0.")
            .ValidateOnStart();
        configure?.Invoke(new OutboxBuilder(whisperBuilder.Services));
        return whisperBuilder;
    }

    /// <summary>
    /// Configures the outbox background worker options such as batch size and polling interval.
    /// </summary>
    public static IOutboxBuilder ConfigureWorker(this IOutboxBuilder outboxBuilder, Action<OutboxWorkerOptions> configure)
    {
        outboxBuilder.Services.Configure(configure);
        return outboxBuilder;
    }

    /// <summary>
    /// Configures how the outbox worker handles failed dispatch attempts: maximum retries,
    /// the delay before the next attempt (e.g. exponential backoff) and which exceptions are
    /// unrecoverable. The effective retry moment is the first poll at or after the scheduled
    /// <see cref="OutboxRecord.NextRetryAtUtc"/>; delays shorter than the polling interval
    /// behave like immediate retries.
    /// </summary>
    public static IOutboxBuilder ConfigureRecoverability(this IOutboxBuilder outboxBuilder, Action<OutboxRecoverabilityOptions> configure)
    {
        outboxBuilder.Services.Configure(configure);
        return outboxBuilder;
    }

    /// <summary>
    /// Configures custom JSON serialization options for the outbox.
    /// Use this to register <see cref="System.Text.Json.Serialization.JsonConverter"/>s
    /// for value types that System.Text.Json cannot deserialize by default.
    /// </summary>
    public static IOutboxBuilder ConfigureSerializer(this IOutboxBuilder outboxBuilder, Action<OutboxJsonOptions> configure)
    {
        outboxBuilder.Services.Configure(configure);
        return outboxBuilder;
    }

    private static void DecorateDispatchersAsInnerDispatchers(IWhisperBuilder whisperBuilder)
    {
        // Keyed registrations (the host's own, or the inner dispatchers of an earlier AddOutbox call) are
        // not part of the request path, so they are left as they are.
        var domainEventDispatcherServiceDescriptors = whisperBuilder.Services
            .Where(s => s.ServiceType == typeof(IDispatchDomainEvents) && !s.IsKeyedService)
            .ToArray();

        foreach (var serviceDescriptor in domainEventDispatcherServiceDescriptors)
        {
            whisperBuilder.Services.Remove(serviceDescriptor);
            AddKeyedFromDescriptor(whisperBuilder.Services, serviceDescriptor, ServiceKeys.InnerDispatcher);
        }
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