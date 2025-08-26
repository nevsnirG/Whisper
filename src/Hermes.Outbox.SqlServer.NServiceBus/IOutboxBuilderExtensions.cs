using Hermes.Outbox.SqlServer;
using Hermes.Outbox.SqlServer.NServiceBus;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder UseNServiceBusStorageSession(this IOutboxBuilder outboxBuilder)
    {
        outboxBuilder.Services.RemoveAll<IConnectionLeaseProvider>();
        outboxBuilder.Services
            .AddScoped<IConnectionLeaseProvider, SynchronizedStorageSessionConnectionLeaseProvider>()
            ;
        return outboxBuilder;
    }
}