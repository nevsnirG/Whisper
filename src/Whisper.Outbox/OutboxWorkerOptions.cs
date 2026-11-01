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

    /// <summary>
    /// How long dispatched records are kept. When set, the worker deletes dispatched records whose
    /// <see cref="OutboxRecord.DispatchedAtUtc"/> is older than this, in small batches about once a minute.
    /// Null (the default) keeps them forever. Failed records are never deleted automatically.
    /// </summary>
    public TimeSpan? RetainDispatchedFor { get; set; }
}
