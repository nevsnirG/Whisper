namespace Whisper.Outbox;

/// <summary>
/// A single failed dispatch attempt: the exception text (already truncated by the worker)
/// and the moment it occurred.
/// </summary>
public sealed record OutboxFailure(string Error, DateTimeOffset OccurredAtUtc);
