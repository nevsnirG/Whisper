using Whisper.Outbox.SqlServer;
using Whisper.Outbox.SqlServer.NServiceBus;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder UseNServiceBusStorageSession(this IOutboxBuilder outboxBuilder)
    {
        outboxBuilder.Services
            .AddScoped<IConnectionLeaseProvider, SqlStorageSessionConnectionLeaseProvider>()
            ;
        return outboxBuilder;
    }
}