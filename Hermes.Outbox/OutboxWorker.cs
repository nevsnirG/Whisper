using Microsoft.Extensions.Hosting;

namespace Hermes.Outbox;

internal sealed class OutboxWorker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        throw new NotImplementedException();
    }
}