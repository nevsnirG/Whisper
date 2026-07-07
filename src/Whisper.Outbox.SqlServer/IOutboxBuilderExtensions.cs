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
            .AddScoped<IOutboxStore>(static sp => new SqlOutboxStore(sp.GetRequiredService<SqlOutboxConfiguration>(), sp.GetService<IConnectionLeaseProvider>()))
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
    /// A registered provider is consulted in EVERY scope, including the background worker's own scopes —
    /// it must yield a usable open connection even when no unit of work is active in that scope,
    /// otherwise the worker cannot read or dispatch outbox records. There is no fallback:
    /// once a provider is registered, it serves every outbox operation.
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
    /// A registered provider is consulted in EVERY scope, including the background worker's own scopes —
    /// it must yield a usable open connection even when no unit of work is active in that scope,
    /// otherwise the worker cannot read or dispatch outbox records. There is no fallback:
    /// once a provider is registered, it serves every outbox operation.
    /// </summary>
    public static IOutboxBuilder UseConnectionLeaseProvider<TProvider>(this IOutboxBuilder outboxBuilder, Func<IServiceProvider, TProvider> factory)
        where TProvider : class, IConnectionLeaseProvider
    {
        outboxBuilder.Services.Replace(ServiceDescriptor.Scoped<TProvider>(factory));
        outboxBuilder.Services.Replace(ServiceDescriptor.Scoped<IConnectionLeaseProvider>(sp => sp.GetRequiredService<TProvider>()));
        return outboxBuilder;
    }
}
