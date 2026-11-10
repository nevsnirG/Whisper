namespace Whisper.Outbox.Abstractions;

/// <summary>
/// A lock in the outbox store that lets the workers of several application instances take turns polling one outbox
/// (see <see cref="OutboxWorkerOptions.ExclusivePolling"/>). A storage backend registers its implementation as a
/// singleton. Without one, every instance polls on its own.
/// </summary>
public interface IOutboxWorkerLock
{
    /// <summary>
    /// Takes the lock if it is free, without waiting. Returns a handle that releases the lock when disposed, or null
    /// when another instance holds it. The worker calls this before every poll and disposes the handle once the batch
    /// is done. Losing the lock while it is held (a dropped connection, an expired lease) can only make a record
    /// dispatch twice, which at-least-once delivery already allows.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquire(CancellationToken cancellationToken);
}
