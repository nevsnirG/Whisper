using Hermes.Outbox.SqlServer;
using Hermes.Outbox.SqlServer.NServiceBus;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder UseNServiceBusStorageSession(this IOutboxBuilder outboxBuilder)
    {
        outboxBuilder.Services
            .AddScoped<IConnectionLeaseProvider, SynchronizedStorageSessionConnectionLeaseProvider>()
            ;
        return outboxBuilder;
    }
}