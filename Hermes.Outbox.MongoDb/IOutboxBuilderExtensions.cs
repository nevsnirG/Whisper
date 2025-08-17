using Hermes.Outbox.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Hermes.Outbox.MongoDb;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder AddOutbox(this IOutboxBuilder outboxBuilder)
    {
        outboxBuilder.Services.AddScoped<IOutboxStore, MongoDbPersister>();
        outboxBuilder.Services.AddScoped<IOutboxReader, MongoDbReader>();
        return outboxBuilder;
    }
}