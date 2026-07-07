namespace Whisper.Outbox.Abstractions;

/// <summary>One page of failed outbox records plus the total number of failed records.</summary>
public sealed record OutboxFailedPage(OutboxRecordSummary[] Records, long TotalCount);
