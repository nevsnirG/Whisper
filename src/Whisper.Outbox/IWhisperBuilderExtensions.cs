using Microsoft.Extensions.DependencyInjection.Extensions;
using Whisper.Abstractions;
using Whisper.Outbox;
using Whisper.Outbox.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;

public static class IWhisperBuilderExtensions
{
    public static IWhisperBuilder AddOutbox(this IWhisperBuilder whisperBuilder, Action<IOutboxBuilder> configure)
    {
        var services = whisperBuilder.Services;

        // Once now, for the dispatchers registered before this call, and again when AddWhisper has finished
        // configuring, for those registered after it. So b.AddMediatR() works on either side of b.AddOutbox(...).
        OutboxDispatcherRegistrations.MoveDispatchersBehindTheOutbox(services);
        whisperBuilder.OnConfigured(OutboxDispatcherRegistrations.MoveDispatchersBehindTheOutbox);
        OutboxDispatcherRegistrations.AddOutboxDispatcher(services);

        services.TryAddTransient<IUuidProvider, DefaultUuidProvider>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OutboxInstallerAwaiter>();
        services.TryAddScoped<OutboxDispatcher>();
        services.TryAddSingleton<IDomainEventSerializer, DomainEventSerializer>();
        services.TryAddSingleton(new OutboxDispatcherRegistrations(services));
        services
            .AddHostedService<OutboxInstaller>()
            .AddHostedService<OutboxWorker>();
        // Only values that break the worker are rejected: BatchSize 0 means "no limit" to the Mongo driver
        // (and selects nothing on SQL Server), PollingIntervalMs -1 is Timeout.Infinite to Task.Delay and
        // other negative intervals throw. A 0 ms interval polls continuously, which works.
        services.AddOptions<OutboxWorkerOptions>()
            .Validate(o => o.BatchSize > 0, $"{nameof(OutboxWorkerOptions)}.{nameof(OutboxWorkerOptions.BatchSize)} must be greater than 0.")
            .Validate(o => o.PollingIntervalMs >= 0, $"{nameof(OutboxWorkerOptions)}.{nameof(OutboxWorkerOptions.PollingIntervalMs)} must not be negative.")
            .Validate(o => o.RetainDispatchedFor is null || o.RetainDispatchedFor > TimeSpan.Zero, $"{nameof(OutboxWorkerOptions)}.{nameof(OutboxWorkerOptions.RetainDispatchedFor)} must be positive when set.")
            .ValidateOnStart();
        configure?.Invoke(new OutboxBuilder(services));
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

    private sealed record OutboxBuilder(IServiceCollection Services) : IOutboxBuilder;
}
