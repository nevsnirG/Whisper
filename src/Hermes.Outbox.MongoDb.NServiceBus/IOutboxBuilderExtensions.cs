using Hermes.Outbox.MongoDb;
using Hermes.Outbox.MongoDb.NServiceBus;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder UseNServiceBusStorageSession(this IOutboxBuilder outboxBuilder)
    {
        outboxBuilder.Services.AddScoped<IMongoSessionProvider, NServiceBusStorageSessionProvider>();
        return outboxBuilder;
    }
}