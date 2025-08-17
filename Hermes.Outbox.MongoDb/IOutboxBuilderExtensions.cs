using Hermes.Outbox.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Hermes.Outbox.MongoDb;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder AddOutbox(this IOutboxBuilder outboxBuilder, MongoOutboxConfiguration mongoOutboxConfiguration)
    {
        RegisterOutboxRecordClassMap();

        var mongoClient = new MongoClient(mongoOutboxConfiguration.ConnectionString);
        outboxBuilder.Services
            .AddScoped<IOutboxStore, MongoDbOutboxStore>(sp =>
            {
                var outboxCollection = GetCollection(mongoOutboxConfiguration, mongoClient);
                return ActivatorUtilities.CreateInstance<MongoDbOutboxStore>(sp, outboxCollection);
            })
            .AddSingleton(mongoOutboxConfiguration);
        return outboxBuilder;
    }

    private static void RegisterOutboxRecordClassMap()
    {
        BsonClassMap.RegisterClassMap<OutboxRecord>(cm =>
        {
            cm.AutoMap();
            cm.MapIdMember(u => u.Id);
        });
    }

    private static IMongoCollection<OutboxRecord> GetCollection(MongoOutboxConfiguration mongoOutboxConfiguration, MongoClient mongoClient)
    {
        var database = mongoClient.GetDatabase(mongoOutboxConfiguration.DatabaseName);
        var indexes = new[]
        {
            new CreateIndexModel<OutboxRecord>(
                Builders<OutboxRecord>.IndexKeys.Ascending(x => x.EnqueuedAtUtc),
                new CreateIndexOptions { Unique = false }),
            new CreateIndexModel<OutboxRecord>(
                Builders<OutboxRecord>.IndexKeys.Ascending(x => x.DispatchedAtUtc),
                new CreateIndexOptions { Unique = false, ExpireAfter = TimeSpan.FromDays(7) }),
        };

        var outboxCollection = database.GetCollection<OutboxRecord>(mongoOutboxConfiguration.CollectionName);
        outboxCollection.Indexes.CreateMany(indexes);
        return outboxCollection;
    }
}