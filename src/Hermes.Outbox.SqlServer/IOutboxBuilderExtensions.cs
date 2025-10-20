using Hermes.Outbox;
using Hermes.Outbox.Abstractions;
using Hermes.Outbox.SqlServer;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
            .AddTransient<IConnectionLeaseProvider, SqlOutboxConfigurationConnectionLeaseProvider>()
            ;
        return outboxBuilder;
    }
}