using Hermes.Outbox.MongoDb;
using Hermes.Outbox.MongoDb.NServiceBus;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder AddMongoDb(this IOutboxBuilder outboxBuilder)
    {
        outboxBuilder.Services.RemoveAll<IMongoSessionProvider>();
        outboxBuilder.Services.AddScoped<IMongoSessionProvider, NServiceBusStorageSessionProvider>();
        return outboxBuilder;
    }
}