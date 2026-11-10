namespace Whisper.Outbox;

public sealed class OutboxWorkerOptions
{
    /// <summary>
    /// When false, this instance still stores events in the outbox but never polls it: nothing is dispatched and no
    /// retention cleanup runs here, so another instance or application sharing the outbox must run the worker.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many records the worker reads per poll. When a full batch dispatches without a failure, the worker reads
    /// the next one right away instead of waiting for <see cref="PollingIntervalMs"/>, so a backlog drains at the
    /// speed of the handlers.
    /// </summary>
    public int BatchSize { get; set; } = 10;

    public int PollingIntervalMs { get; set; } = 1000;

    /// <summary>
    /// When false, the polling delay between batches is driven by the wall clock instead
    /// of the registered <see cref="TimeProvider"/>.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool UsePollingTimeProvider { get; set; } = true;

    /// <summary>
    /// When true (the default), application instances that share an outbox take turns polling it, through a lock in
    /// the outbox store. Two instances then never dispatch the same record at the same time, and records keep their
    /// order. Set to false to let every instance poll on its own; a record is then dispatched by every instance that
    /// reads it before another one has marked it dispatched.
    /// </summary>
    public bool ExclusivePolling { get; set; } = true;

    /// <summary>
    /// How long dispatched records are kept. When set, the worker deletes dispatched records whose
    /// <see cref="OutboxRecord.DispatchedAtUtc"/> is older than this, in small batches about once a minute.
    /// Null (the default) keeps them forever. Failed records are never deleted automatically.
    /// </summary>
    public TimeSpan? RetainDispatchedFor { get; set; }
}
