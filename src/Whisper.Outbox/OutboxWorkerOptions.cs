namespace Whisper.Outbox;
public sealed class OutboxWorkerOptions
{
    public int BatchSize { get; set; } = 10;
    public int PollingIntervalMs { get; set; } = 1000;
    public int MaxRetries { get; set; } = 3;
}
