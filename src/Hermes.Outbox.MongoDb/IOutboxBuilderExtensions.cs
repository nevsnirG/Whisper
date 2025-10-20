using Hermes.Outbox;
using Hermes.Outbox.Abstractions;
using Hermes.Outbox.MongoDb;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder AddMongoDb(this IOutboxBuilder outboxBuilder, MongoDbOutboxConfiguration mongoDbOutboxConfiguration)
    {
        RegisterOutboxRecordClassMap();

        outboxBuilder.Services
            .AddScoped<IOutboxStore, MongoDbOutboxStore>(static sp =>
            {
                var outboxCollection = sp.GetOutboxCollection();
                return ActivatorUtilities.CreateInstance<MongoDbOutboxStore>(sp, outboxCollection);
            })
            .AddSingleton(mongoDbOutboxConfiguration)
            .AddSingleton(new MongoClient(mongoDbOutboxConfiguration.ConnectionString))
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