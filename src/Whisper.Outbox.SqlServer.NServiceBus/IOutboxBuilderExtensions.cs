using Whisper.Outbox;
using Whisper.Outbox.SqlServer.NServiceBus;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder UseNServiceBusStorageSession(this IOutboxBuilder outboxBuilder)
    {
        return outboxBuilder.UseConnectionLeaseProvider<SqlStorageSessionConnectionLeaseProvider>();
    }
}
