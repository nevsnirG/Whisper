using Microsoft.Extensions.DependencyInjection.Extensions;
using Whisper.Outbox;
using Whisper.Outbox.MongoDb;
using Whisper.Outbox.MongoDb.NServiceBus;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder UseNServiceBusStorageSession(this IOutboxBuilder outboxBuilder)
    {
        outboxBuilder.Services.Replace(ServiceDescriptor.Scoped<IMongoSessionProvider, NServiceBusStorageSessionProvider>());
        return outboxBuilder;
    }
}