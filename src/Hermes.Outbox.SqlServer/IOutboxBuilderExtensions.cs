using Hermes.Outbox;
using Hermes.Outbox.Abstractions;
using Hermes.Outbox.SqlServer;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder AddSqlServer(this IOutboxBuilder outboxBuilder, SqlOutboxConfiguration sqlOutboxConfiguration)
    {
        outboxBuilder.Services
            .AddScoped<IOutboxStore, SqlOutboxStore>()
            .AddSingleton(sqlOutboxConfiguration)
            .AddTransient<IInstallOutbox, SqlOutboxInstaller>()
            .AddTransient<IUuidProvider, SqlServerUuidProvider>()
            ;
        return outboxBuilder;
    }
}