namespace Whisper.Outbox;
public sealed class OutboxWorkerOptions
{
    public int BatchSize { get; set; } = 10;
    public int PollingIntervalMs { get; set; } = 1000;

    /// <summary>
    /// When true, the polling delay between batches is driven by the registered <see cref="TimeProvider"/>
    /// instead of the wall clock, so tests can advance polling deterministically with a fake time provider.
    /// Defaults to false: polling always waits in real time, even when a fake <see cref="TimeProvider"/>
    /// is registered globally. Timestamps (batch due-at reads, dispatched-at, failure times and retry
    /// scheduling) always use the registered <see cref="TimeProvider"/> regardless of this setting.
    /// </summary>
    public bool UsePollingTimeProvider { get; set; }
}
