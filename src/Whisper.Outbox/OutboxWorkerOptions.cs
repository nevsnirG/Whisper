namespace Whisper.Outbox;

public sealed class OutboxWorkerOptions
{
    public int BatchSize { get; set; } = 10;
    public int PollingIntervalMs { get; set; } = 1000;

    /// <summary>
    /// When false, the polling delay between batches is driven by the wall clock instead
    /// of the registered <see cref="TimeProvider"/>.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool UsePollingTimeProvider { get; set; } = true;
}
