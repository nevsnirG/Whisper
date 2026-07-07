namespace Whisper.Outbox;
public class OutboxRecord
{
    public required Guid Id { get; init; }
    public required DateTimeOffset EnqueuedAtUtc { get; init; }
    public DateTimeOffset? DispatchedAtUtc { get; set; }
    public DateTimeOffset? FailedAtUtc { get; set; }
    public int Retries { get; set; }
    public required string AssemblyQualifiedType { get; init; }
    public required string Payload { get; init; }

    /// <summary>
    /// The last dispatch failure (<see cref="Exception.ToString"/>, truncated), persisted on every failed attempt.
    /// Kept after a successful manual retry as an audit trail.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>When <see cref="LastError"/> was recorded.</summary>
    public DateTimeOffset? LastErrorAtUtc { get; set; }

    /// <summary>Earliest moment the record is eligible for dispatch again; null = eligible now.</summary>
    public DateTimeOffset? NextRetryAtUtc { get; set; }
}
