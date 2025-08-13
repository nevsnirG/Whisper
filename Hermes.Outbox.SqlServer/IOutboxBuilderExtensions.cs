using Microsoft.Extensions.DependencyInjection;

namespace Hermes.Outbox.SqlServer;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder AddOutbox(this IOutboxBuilder outboxBuilder)
    {
        outboxBuilder.Services.AddScoped<IOutboxPersister, SqlServerPersister>();
        outboxBuilder.Services.AddScoped<IOutboxReader, SqlServerReader>();
        return outboxBuilder;
    }
}