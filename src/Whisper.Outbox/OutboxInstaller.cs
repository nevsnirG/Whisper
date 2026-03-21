using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox;
internal sealed class OutboxInstaller(IInstallOutbox outboxInstaller,
                                      OutboxInstallerAwaiter outboxInstallerAwaiter,
                                      ILogger<OutboxInstaller> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Installing outbox storage...");
        await outboxInstaller.InstallCollection(cancellationToken);
        outboxInstallerAwaiter.SignalCompletion();
        logger.LogInformation("Outbox storage installed successfully.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
