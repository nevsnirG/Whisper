namespace Whisper.Outbox.Abstractions;

/// <summary>
/// A failed outbox record for listing purposes: without its payload, and with only the first
/// <see cref="LastErrorPreviewLength"/> characters of <see cref="OutboxRecord.LastError"/> in
/// <see cref="LastError"/>. A list shows one line per record, and the full text (up to 32,768 characters each)
/// is on the record itself.
/// </summary>
public sealed record OutboxRecordSummary(
    Guid Id,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset FailedAtUtc,
    int Retries,
    string AssemblyQualifiedType,
    string? LastError,
    DateTimeOffset? LastErrorAtUtc)
{
    /// <summary>How much of <see cref="OutboxRecord.LastError"/> a summary carries.</summary>
    public const int LastErrorPreviewLength = 512;
}
