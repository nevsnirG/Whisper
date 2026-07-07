using Whisper.Outbox;
using Whisper.Outbox.MongoDb.NServiceBus;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder UseNServiceBusStorageSession(this IOutboxBuilder outboxBuilder)
    {
        return outboxBuilder.UseMongoSessionProvider<NServiceBusStorageSessionProvider>();
    }
}
