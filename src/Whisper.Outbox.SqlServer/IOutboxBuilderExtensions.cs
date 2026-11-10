using Whisper.Outbox.Abstractions;
using Whisper.Outbox.SqlServer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whisper.Outbox;

namespace Microsoft.Extensions.DependencyInjection;
public static class IOutboxBuilderExtensions
{
    public static IOutboxBuilder AddSqlServer(this IOutboxBuilder outboxBuilder, SqlOutboxConfiguration sqlOutboxConfiguration)
    {
        outboxBuilder.Services
            .AddScoped<IOutboxStore>(static sp => new SqlOutboxStore(sp.GetRequiredService<SqlOutboxConfiguration>(), new DeferredConnectionLeaseProvider(sp)))
            .AddSingleton<IOutboxManagementStore, SqlOutboxManagementStore>()
            .AddSingleton<IOutboxWorkerLock, SqlOutboxWorkerLock>()
            .AddSingleton(sqlOutboxConfiguration)
            .AddTransient<IInstallOutbox, SqlOutboxInstaller>()
            .Replace(ServiceDescriptor.Transient<IUuidProvider, SqlServerUuidProvider>())
            ;
        return outboxBuilder;
    }

    /// <summary>
    /// Registers <typeparamref name="TProvider"/> as the source of the SQL connection (and optional transaction) used by the outbox store.
    /// The host owns the lifecycle of every connection and transaction the provider yields;
    /// Whisper only uses them and never opens, commits, rolls back or disposes them.
    /// The provider is only asked when events are stored. It returns null in a scope without a unit of work, and the
    /// store then opens and disposes its own connection for that write. The background worker never asks it: its
    /// reads and updates always run on a connection of Whisper's own.
    /// </summary>
    public static IOutboxBuilder UseConnectionLeaseProvider<TProvider>(this IOutboxBuilder outboxBuilder)
        where TProvider : class, IConnectionLeaseProvider
    {
        outboxBuilder.Services.Replace(ServiceDescriptor.Scoped<TProvider, TProvider>());
        outboxBuilder.Services.Replace(ServiceDescriptor.Scoped<IConnectionLeaseProvider>(sp => sp.GetRequiredService<TProvider>()));
        return outboxBuilder;
    }

    /// <summary>
    /// Registers <typeparamref name="TProvider"/> as the source of the SQL connection (and optional transaction) used by the outbox store.
    /// The host owns the lifecycle of every connection and transaction the provider yields;
    /// Whisper only uses them and never opens, commits, rolls back or disposes them.
    /// The provider is only asked when events are stored. It returns null in a scope without a unit of work, and the
    /// store then opens and disposes its own connection for that write. The background worker never asks it: its
    /// reads and updates always run on a connection of Whisper's own.
    /// </summary>
    public static IOutboxBuilder UseConnectionLeaseProvider<TProvider>(this IOutboxBuilder outboxBuilder, Func<IServiceProvider, TProvider> factory)
        where TProvider : class, IConnectionLeaseProvider
    {
        outboxBuilder.Services.Replace(ServiceDescriptor.Scoped<TProvider>(factory));
        outboxBuilder.Services.Replace(ServiceDescriptor.Scoped<IConnectionLeaseProvider>(sp => sp.GetRequiredService<TProvider>()));
        return outboxBuilder;
    }
}
