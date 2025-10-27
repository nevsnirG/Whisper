using Microsoft.Extensions.Hosting;

namespace Whisper.Outbox;
internal sealed class OutboxInstaller(IInstallOutbox outboxInstaller, OutboxInstallerAwaiter outboxInstallerAwaiter) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await outboxInstaller.InstallCollection(cancellationToken);
        outboxInstallerAwaiter.SignalCompletion();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}