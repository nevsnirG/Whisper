using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.AspNetCore;

// Wire contracts local to this package, decoupling the HTTP contract from the storage types.

internal sealed record FailedRecordsPageResponse(FailedRecordResponse[] Records, long TotalCount, int Page, int PageSize)
{
    internal static FailedRecordsPageResponse From(OutboxFailedPage failedPage, int page, int pageSize)
    {
        var records = Array.ConvertAll(failedPage.Records, FailedRecordResponse.From);
        return new(records, failedPage.TotalCount, page, pageSize);
    }
}

internal sealed record FailedRecordResponse(
    Guid Id,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset FailedAtUtc,
    int Retries,
    string AssemblyQualifiedType,
    string? LastError,
    DateTimeOffset? LastErrorAtUtc)
{
    internal static FailedRecordResponse From(OutboxRecordSummary summary)
    {
        return new(
            summary.Id,
            summary.EnqueuedAtUtc,
            summary.FailedAtUtc,
            summary.Retries,
            summary.AssemblyQualifiedType,
            summary.LastError,
            summary.LastErrorAtUtc);
    }
}

internal sealed record OutboxRecordResponse(
    Guid Id,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? FailedAtUtc,
    int Retries,
    string AssemblyQualifiedType,
    string Payload,
    string? LastError,
    DateTimeOffset? LastErrorAtUtc,
    DateTimeOffset? NextRetryAtUtc)
{
    internal static OutboxRecordResponse From(OutboxRecord record)
    {
        return new(
            record.Id,
            record.EnqueuedAtUtc,
            record.DispatchedAtUtc,
            record.FailedAtUtc,
            record.Retries,
            record.AssemblyQualifiedType,
            record.Payload,
            record.LastError,
            record.LastErrorAtUtc,
            record.NextRetryAtUtc);
    }
}

internal sealed record RetryAllResponse(long Retried);
