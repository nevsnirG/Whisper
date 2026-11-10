using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox;
internal sealed class OutboxInstaller(IInstallOutbox outboxInstaller,
                                      OutboxInstallerAwaiter outboxInstallerAwaiter,
                                      OutboxDispatcherRegistrations dispatcherRegistrations,
                                      IOptions<OutboxWorkerOptions> outboxWorkerOptions,
                                      ILogger<OutboxInstaller> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CheckDispatchers();

        logger.LogInformation("Installing outbox storage...");
        await outboxInstaller.InstallCollection(cancellationToken);
        outboxInstallerAwaiter.SignalCompletion();
        logger.LogInformation("Outbox storage installed successfully.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // A worker with nothing to dispatch to would mark every record as dispatched without delivering it, so that
    // configuration must not start.
    private void CheckDispatchers()
    {
        var besideTheOutbox = dispatcherRegistrations.RequestPathDispatchersBesideTheOutbox();
        if (besideTheOutbox.Length > 0)
        {
            logger.LogWarning(
                "{Dispatchers} was registered as IDispatchDomainEvents after AddWhisper configured the outbox. " +
                "It runs in the request path next to the outbox, and the outbox worker does not dispatch to it. " +
                "Register dispatchers inside AddWhisper (or before it) to have the outbox deliver to them.",
                string.Join(", ", besideTheOutbox));
        }

        if (outboxWorkerOptions.Value.Enabled && dispatcherRegistrations.InnerDispatchers().Length == 0)
        {
            throw new InvalidOperationException(
                "The outbox has no dispatcher to deliver stored domain events to, so its worker would mark every event as dispatched without delivering it. " +
                "Register a dispatcher inside AddWhisper (for example b.AddMediatR()), " +
                $"or set {nameof(OutboxWorkerOptions)}.{nameof(OutboxWorkerOptions.Enabled)} to false on an instance that only stores events.");
        }
    }
}
