namespace Whisper.Outbox.Abstractions;

/// <summary>A failed outbox record without its payload, for listing purposes.</summary>
public sealed record OutboxRecordSummary(
    Guid Id,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset FailedAtUtc,
    int Retries,
    string AssemblyQualifiedType,
    string? LastError,
    DateTimeOffset? LastErrorAtUtc);
