using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using Whisper.Outbox;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.MongoDb;

namespace Microsoft.Extensions.DependencyInjection;

public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder AddMongoDb(this IOutboxBuilder outboxBuilder, MongoDbOutboxConfiguration mongoDbOutboxConfiguration)
    {
        return AddMongoDb(outboxBuilder, _ => mongoDbOutboxConfiguration);
    }

    public static IOutboxBuilder AddMongoDb(this IOutboxBuilder outboxBuilder, Func<IServiceProvider, MongoDbOutboxConfiguration> mongoDbOutboxConfiguration)
    {
        RegisterOutboxRecordClassMap();

        outboxBuilder.Services
            .AddScoped<IOutboxStore>(static sp => new MongoDbOutboxStore(sp.GetOutboxCollection(), new DeferredMongoSessionProvider(sp)))
            .AddSingleton<IOutboxManagementStore>(static sp => new MongoDbOutboxManagementStore(sp.GetOutboxCollection()))
            .AddSingleton<IOutboxWorkerLock>(static sp => new MongoDbOutboxWorkerLock(
                sp.GetOutboxLockCollection(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<MongoDbOutboxWorkerLock>>() ?? NullLogger<MongoDbOutboxWorkerLock>.Instance))
            .AddSingleton(mongoDbOutboxConfiguration)
            .AddSingleton(static sp => new OutboxMongoClient(new MongoClient(sp.GetRequiredService<MongoDbOutboxConfiguration>().ConnectionString)))
            .AddTransient<IInstallOutbox, MongoDbOutboxInstaller>()
            ;
        // Only for hosts that resolved Whisper's client as MongoClient before 4.2.0; Whisper itself never uses this
        // registration. TryAdd, so a MongoClient the host registered first is left alone.
        outboxBuilder.Services.TryAddSingleton(static sp => sp.GetRequiredService<OutboxMongoClient>().Client);
        return outboxBuilder;
    }

    /// <summary>
    /// Registers <typeparamref name="TProvider"/> as the source of the Mongo session used by the outbox store.
    /// The host owns the session lifecycle; Whisper only uses the session it yields and never starts,
    /// commits, aborts or disposes it.
    /// Any existing <see cref="Whisper.Outbox.MongoDb.IMongoSessionProviderInitializer"/> registration is removed,
    /// including one the host registered directly in DI — the last Use* call owns the initializer slot.
    /// </summary>
    public static IOutboxBuilder UseMongoSessionProvider<TProvider>(this IOutboxBuilder outboxBuilder)
        where TProvider : class, IMongoSessionProvider
    {
        outboxBuilder.Services.Replace(ServiceDescriptor.Scoped<TProvider, TProvider>());
        RegisterProviderForwarding<TProvider>(outboxBuilder.Services);
        return outboxBuilder;
    }

    /// <summary>
    /// Registers <typeparamref name="TProvider"/> as the source of the Mongo session used by the outbox store.
    /// The host owns the session lifecycle; Whisper only uses the session it yields and never starts,
    /// commits, aborts or disposes it.
    /// Any existing <see cref="Whisper.Outbox.MongoDb.IMongoSessionProviderInitializer"/> registration is removed,
    /// including one the host registered directly in DI — the last Use* call owns the initializer slot.
    /// </summary>
    public static IOutboxBuilder UseMongoSessionProvider<TProvider>(this IOutboxBuilder outboxBuilder, Func<IServiceProvider, TProvider> factory)
        where TProvider : class, IMongoSessionProvider
    {
        outboxBuilder.Services.Replace(ServiceDescriptor.Scoped<TProvider>(factory));
        RegisterProviderForwarding<TProvider>(outboxBuilder.Services);
        return outboxBuilder;
    }

    /// <summary>
    /// Lets the host hand its own Mongo session to the outbox. The host resolves
    /// <see cref="Whisper.Outbox.MongoDb.IMongoSessionProviderInitializer"/> inside its unit of work and calls
    /// <see cref="Whisper.Outbox.MongoDb.IMongoSessionProviderInitializer.Initialize(IClientSessionHandle)"/> with its session;
    /// a scope that never initializes gets plain (session-less) writes.
    /// The host owns the session lifecycle; Whisper only uses the session and never starts, commits, aborts or disposes it.
    /// Any existing <see cref="Whisper.Outbox.MongoDb.IMongoSessionProviderInitializer"/> registration is removed,
    /// including one the host registered directly in DI — the last Use* call owns the initializer slot.
    /// </summary>
    public static IOutboxBuilder UseHostManagedMongoSession(this IOutboxBuilder outboxBuilder)
    {
        return outboxBuilder.UseMongoSessionProvider<MongoSessionProvider>();
    }

    private static void RegisterProviderForwarding<TProvider>(IServiceCollection services)
        where TProvider : class, IMongoSessionProvider
    {
        services.Replace(ServiceDescriptor.Scoped<IMongoSessionProvider>(sp => sp.GetRequiredService<TProvider>()));
        services.RemoveAll<IMongoSessionProviderInitializer>();
        if (typeof(IMongoSessionProviderInitializer).IsAssignableFrom(typeof(TProvider)))
            services.AddScoped<IMongoSessionProviderInitializer>(static sp => (IMongoSessionProviderInitializer)sp.GetRequiredService<IMongoSessionProvider>());
    }

    private static IMongoCollection<OutboxRecord> GetOutboxCollection(this IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<MongoDbOutboxConfiguration>();
        return serviceProvider.GetOutboxDatabase().GetCollection<OutboxRecord>(configuration.CollectionName);
    }

    // The worker lock lives next to the outbox, not in it: every document in the outbox collection is a record.
    private static IMongoCollection<BsonDocument> GetOutboxLockCollection(this IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<MongoDbOutboxConfiguration>();
        return serviceProvider.GetOutboxDatabase().GetCollection<BsonDocument>($"{configuration.CollectionName}.locks");
    }

    private static IMongoDatabase GetOutboxDatabase(this IServiceProvider serviceProvider)
    {
        var mongoClient = serviceProvider.GetRequiredService<OutboxMongoClient>().Client;
        var configuration = serviceProvider.GetRequiredService<MongoDbOutboxConfiguration>();
        return mongoClient.GetDatabase(configuration.DatabaseName);
    }

    private static void RegisterOutboxRecordClassMap()
    {
        BsonClassMap.TryRegisterClassMap<OutboxRecord>(cm =>
        {
            cm.AutoMap();
            // A version that adds a field must not stop this one from reading the collection, which is what happens
            // while both run side by side in a rolling deployment, and after a rollback.
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(u => u.Id)
                .SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
            // These three are stored the way they always were: as a {DateTime, Ticks, Offset} document, the driver's
            // default for DateTimeOffset. Stated here so a different default can never split one collection into
            // two representations, which filters and sorts cannot compare with each other.
            cm.MapMember(u => u.EnqueuedAtUtc)
                .SetSerializer(new DateTimeOffsetSerializer(BsonType.Document));
            cm.MapMember(u => u.DispatchedAtUtc)
                .SetSerializer(new NullableSerializer<DateTimeOffset>(new DateTimeOffsetSerializer(BsonType.Document)));
            cm.MapMember(u => u.FailedAtUtc)
                .SetSerializer(new NullableSerializer<DateTimeOffset>(new DateTimeOffsetSerializer(BsonType.Document)));
            // BsonType.DateTime is mandatory here: $lte filters and sorts on these members need plain BSON dates,
            // not the driver's default DateTimeOffset representation (a {DateTime, Ticks, Offset} document).
            cm.MapMember(u => u.NextRetryAtUtc)
                .SetSerializer(new NullableSerializer<DateTimeOffset>(new DateTimeOffsetSerializer(BsonType.DateTime)));
            cm.MapMember(u => u.LastErrorAtUtc)
                .SetSerializer(new NullableSerializer<DateTimeOffset>(new DateTimeOffsetSerializer(BsonType.DateTime)));
        });
    }
}
