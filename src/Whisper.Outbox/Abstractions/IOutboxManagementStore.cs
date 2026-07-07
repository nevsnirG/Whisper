namespace Whisper.Outbox.Abstractions;

/// <summary>
/// Storage access for outbox management (dashboard) operations on failed records.
/// Implementations are singletons with zero dependencies on the ambient scoped session/connection
/// providers and own their storage access entirely, making them immune to host unit-of-work
/// middleware, ambient transactions and pipeline ordering.
/// </summary>
public interface IOutboxManagementStore
{
    /// <summary>
    /// Pages failed records ordered by <see cref="OutboxRecord.FailedAtUtc"/> descending.
    /// <paramref name="page"/> is 1-based (values below 1 are clamped to 1);
    /// <paramref name="pageSize"/> is clamped to 1..200. Summaries never include the payload.
    /// </summary>
    Task<OutboxFailedPage> GetFailed(int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the full record including <see cref="OutboxRecord.Payload"/> and <see cref="OutboxRecord.LastError"/>,
    /// or null when it does not exist.
    /// </summary>
    Task<OutboxRecord?> Get(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Re-queues a FAILED record for dispatch: resets <see cref="OutboxRecord.FailedAtUtc"/>,
    /// <see cref="OutboxRecord.Retries"/> and <see cref="OutboxRecord.NextRetryAtUtc"/>, keeping
    /// <see cref="OutboxRecord.LastError"/> as an audit trail. Returns false when the record is
    /// missing or not failed — the worker and the dashboard can never fight over a record.
    /// </summary>
    Task<bool> Retry(Guid id, CancellationToken cancellationToken);

    /// <summary>Re-queues all failed records (same reset semantics as <see cref="Retry"/>) and returns the count.</summary>
    Task<long> RetryAll(CancellationToken cancellationToken);

    /// <summary>Deletes a FAILED record. Returns false when the record is missing or not failed.</summary>
    Task<bool> Delete(Guid id, CancellationToken cancellationToken);
}
