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
#pragma warning disable CS0618 // Type or member is obsolete
        BsonDefaults.GuidRepresentationMode = GuidRepresentationMode.V3;
#pragma warning restore CS0618 // Type or member is obsolete

        outboxBuilder.Services
            .AddScoped<IOutboxStore, MongoDbOutboxStore>(static sp =>
            {
                var outboxCollection = sp.GetOutboxCollection();
                return ActivatorUtilities.CreateInstance<MongoDbOutboxStore>(sp, outboxCollection);
            })
            .AddSingleton(mongoDbOutboxConfiguration)
            .AddSingleton(sp => new MongoClient(sp.GetRequiredService<MongoDbOutboxConfiguration>().ConnectionString))
            .AddTransient<IInstallOutbox, MongoDbOutboxInstaller>()
            .AddScoped<IMongoSessionProvider, EmptyMongoSessionProvider>()
            ;
        return outboxBuilder;
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
        BsonClassMap.RegisterClassMap<OutboxRecord>(cm =>
        {
            cm.AutoMap();
            cm.MapIdMember(u => u.Id)
                .SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
        });
    }
}