using Microsoft.Data.SqlClient;
using System.Data;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer;

internal sealed class SqlOutboxStore(SqlOutboxConfiguration sqlOutboxConfiguration, IConnectionLeaseProvider? connectionLeaseProvider) : IOutboxStore
{
    public async Task Add(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        var sql = $@"
INSERT INTO {QualifiedTableName()}
(Id, EnqueuedAtUtc, AssemblyQualifiedType, Payload)
VALUES (@Id, @EnqueuedAtUtc, @AssemblyQualifiedType, @Payload);";

        await ExecuteAsync(sql, cmd =>
        {
            cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = outboxRecord.Id });
            cmd.Parameters.Add(new SqlParameter("@EnqueuedAtUtc", SqlDbType.DateTimeOffset) { Value = outboxRecord.EnqueuedAtUtc });
            cmd.Parameters.Add(new SqlParameter("@AssemblyQualifiedType", SqlDbType.NVarChar, 2048) { Value = outboxRecord.AssemblyQualifiedType });
            cmd.Parameters.Add(new SqlParameter("@Payload", SqlDbType.NVarChar, -1) { Value = outboxRecord.Payload });
        }, cancellationToken);
    }

    public async Task Add(OutboxRecord[] outboxRecords, CancellationToken cancellationToken)
    {
        var sql = $@"
INSERT INTO {QualifiedTableName()}
(Id, EnqueuedAtUtc, AssemblyQualifiedType, Payload)
VALUES (@Id, @EnqueuedAtUtc, @AssemblyQualifiedType, @Payload);";

        await using var leaseScope = await AcquireLease(cancellationToken);
        using var cmd = new SqlCommand(sql, leaseScope.Connection, leaseScope.Transaction);

        var pId = cmd.Parameters.Add("@Id", SqlDbType.UniqueIdentifier);
        var pEnqueued = cmd.Parameters.Add("@EnqueuedAtUtc", SqlDbType.DateTimeOffset);
        var pType = cmd.Parameters.Add("@AssemblyQualifiedType", SqlDbType.NVarChar, 2048);
        var pPayload = cmd.Parameters.Add("@Payload", SqlDbType.NVarChar, -1);

        foreach (var r in outboxRecords)
        {
            pId.Value = r.Id;
            pEnqueued.Value = r.EnqueuedAtUtc;
            pType.Value = r.AssemblyQualifiedType;
            pPayload.Value = r.Payload;

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<OutboxRecord[]> ReadNextBatch(int batchSize, DateTimeOffset dueAtUtc, CancellationToken cancellationToken)
    {
        var sql = $@"
SELECT TOP (@take)
    Id, EnqueuedAtUtc, Retries, AssemblyQualifiedType, Payload
FROM {QualifiedTableName()} WITH (READCOMMITTEDLOCK)
WHERE DispatchedAtUtc IS NULL AND FailedAtUtc IS NULL
  AND (NextRetryAtUtc IS NULL OR NextRetryAtUtc <= @dueAtUtc)
ORDER BY NextRetryAtUtc ASC, Id ASC;";

        await using var leaseScope = await AcquireLease(cancellationToken);
        using var cmd = new SqlCommand(sql, leaseScope.Connection, leaseScope.Transaction);
        cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = batchSize });
        cmd.Parameters.Add(new SqlParameter("@dueAtUtc", SqlDbType.DateTimeOffset) { Value = dueAtUtc });

        var list = new List<OutboxRecord>(batchSize);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new OutboxRecord
            {
                Id = reader.GetGuid(0),
                EnqueuedAtUtc = reader.GetFieldValue<DateTimeOffset>(1),
                Retries = reader.GetInt32(2),
                AssemblyQualifiedType = reader.GetString(3),
                Payload = reader.GetString(4),
            });
        }

        return [.. list];
    }

    public Task SetDispatchedAt(OutboxRecord outboxRecord, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken)
    {
        return ExecuteUpdateByIdAsync("SET DispatchedAtUtc = @value", outboxRecord.Id, cmd =>
            cmd.Parameters.Add(new SqlParameter("@value", SqlDbType.DateTimeOffset) { Value = dispatchedAtUtc }),
            cancellationToken);
    }

    public Task ScheduleRetry(OutboxRecord outboxRecord, OutboxFailure failure, DateTimeOffset? nextRetryAtUtc, CancellationToken cancellationToken)
    {
        return ExecuteUpdateByIdAsync(
            "SET Retries = Retries + 1, NextRetryAtUtc = @nextRetryAtUtc, LastError = @lastError, LastErrorAtUtc = @lastErrorAtUtc",
            outboxRecord.Id,
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@nextRetryAtUtc", SqlDbType.DateTimeOffset) { Value = (object?)nextRetryAtUtc ?? DBNull.Value });
                AddFailureParameters(cmd, failure);
            },
            cancellationToken);
    }

    public Task SetFailedAt(OutboxRecord outboxRecord, OutboxFailure failure, CancellationToken cancellationToken)
    {
        return ExecuteUpdateByIdAsync(
            "SET FailedAtUtc = @failedAtUtc, LastError = @lastError, LastErrorAtUtc = @lastErrorAtUtc",
            outboxRecord.Id,
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@failedAtUtc", SqlDbType.DateTimeOffset) { Value = failure.OccurredAtUtc });
                AddFailureParameters(cmd, failure);
            },
            cancellationToken);
    }

    private static void AddFailureParameters(SqlCommand cmd, OutboxFailure failure)
    {
        cmd.Parameters.Add(new SqlParameter("@lastError", SqlDbType.NVarChar, -1) { Value = failure.Error });
        cmd.Parameters.Add(new SqlParameter("@lastErrorAtUtc", SqlDbType.DateTimeOffset) { Value = failure.OccurredAtUtc });
    }

    private Task ExecuteUpdateByIdAsync(string setClause, Guid id, Action<SqlCommand> addParameters, CancellationToken cancellationToken)
    {
        var sql = $@"
UPDATE {QualifiedTableName()}
{setClause}
WHERE Id = @id;";

        return ExecuteAsync(sql, cmd =>
        {
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            addParameters(cmd);
        }, cancellationToken);
    }

    private async Task ExecuteAsync(string sql, Action<SqlCommand> addParameters, CancellationToken cancellationToken)
    {
        await using var leaseScope = await AcquireLease(cancellationToken);
        using var cmd = new SqlCommand(sql, leaseScope.Connection, leaseScope.Transaction);
        addParameters(cmd);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<LeaseScope> AcquireLease(CancellationToken cancellationToken)
    {
        if (connectionLeaseProvider is not null)
            return new LeaseScope(await connectionLeaseProvider.Provide(cancellationToken), OwnsConnection: false);

        var connection = new SqlConnection(sqlOutboxConfiguration.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return new LeaseScope(new ConnectionLease(connection), OwnsConnection: true);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private readonly record struct LeaseScope(ConnectionLease Lease, bool OwnsConnection) : IAsyncDisposable
    {
        public SqlConnection Connection => Lease.Connection;

        public SqlTransaction? Transaction => Lease.Transaction;

        public ValueTask DisposeAsync()
            => OwnsConnection ? Lease.Connection.DisposeAsync() : ValueTask.CompletedTask;
    }

    private string QualifiedTableName()
        => $"{SqlIdentifier.Bracket(sqlOutboxConfiguration.SchemaName)}.{SqlIdentifier.Bracket(sqlOutboxConfiguration.TableName)}";
}
