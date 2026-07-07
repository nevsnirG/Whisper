namespace Whisper.Outbox;

/// <summary>
/// Controls how the outbox worker handles failed dispatch attempts.
/// </summary>
public sealed class OutboxRecoverabilityOptions
{
    /// <summary>
    /// Maximum total attempts before a record is marked permanently failed
    /// (a record fails when <c>Retries + 1 &gt;= MaxRetries</c>).
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Maps the 1-based failed-attempt ordinal to the delay before the next attempt
    /// (e.g. exponential backoff). Null (the default) or a non-positive delay means the record is
    /// eligible again at the next poll. The effective retry moment is the first poll at or after
    /// <see cref="OutboxRecord.NextRetryAtUtc"/>; delays shorter than the polling interval behave
    /// like immediate retries.
    /// </summary>
    public Func<int, TimeSpan>? RetryDelay { get; set; }

    /// <summary>
    /// Exception types (including derived types) that permanently fail a record on the first occurrence.
    /// </summary>
    public ICollection<Type> UnrecoverableExceptionTypes { get; } = [];

    /// <summary>
    /// Predicates that mark an exception unrecoverable. A throwing predicate is logged and treated as non-matching.
    /// </summary>
    public ICollection<Func<Exception, bool>> UnrecoverableExceptionPredicates { get; } = [];
}
