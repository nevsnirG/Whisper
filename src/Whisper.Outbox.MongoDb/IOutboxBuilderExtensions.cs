using Microsoft.Extensions.DependencyInjection.Extensions;
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
            .AddScoped<IOutboxStore>(static sp => new MongoDbOutboxStore(sp.GetOutboxCollection(), sp.GetService<IMongoSessionProvider>()))
            .AddSingleton<IOutboxManagementStore>(static sp => new MongoDbOutboxManagementStore(sp.GetOutboxCollection()))
            .AddSingleton(mongoDbOutboxConfiguration)
            .AddSingleton(sp => new MongoClient(sp.GetRequiredService<MongoDbOutboxConfiguration>().ConnectionString))
            .AddTransient<IInstallOutbox, MongoDbOutboxInstaller>()
            ;
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
        var mongoClient = serviceProvider.GetRequiredService<MongoClient>();
        var configuration = serviceProvider.GetRequiredService<MongoDbOutboxConfiguration>();
        var database = mongoClient.GetDatabase(configuration.DatabaseName);
        return database.GetCollection<OutboxRecord>(configuration.CollectionName);
    }

    private static void RegisterOutboxRecordClassMap()
    {
        BsonClassMap.TryRegisterClassMap<OutboxRecord>(cm =>
        {
            cm.AutoMap();
            cm.MapIdMember(u => u.Id)
                .SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
            // BsonType.DateTime is mandatory here: the driver's default DateTimeOffset (array)
            // representation breaks $lte filters and sorts on these members.
            cm.MapMember(u => u.NextRetryAtUtc)
                .SetSerializer(new NullableSerializer<DateTimeOffset>(new DateTimeOffsetSerializer(BsonType.DateTime)));
            cm.MapMember(u => u.LastErrorAtUtc)
                .SetSerializer(new NullableSerializer<DateTimeOffset>(new DateTimeOffsetSerializer(BsonType.DateTime)));
        });
    }
}
