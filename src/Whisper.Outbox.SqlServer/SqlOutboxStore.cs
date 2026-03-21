using Microsoft.Data.SqlClient;
using System.Data;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer;

internal sealed class SqlOutboxStore(SqlOutboxConfiguration sqlOutboxConfiguration, IConnectionLeaseProvider connectionLeaseProvider) : IOutboxStore
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

        await using var connectionLease = await connectionLeaseProvider.Provide(cancellationToken);
        using var cmd = new SqlCommand(sql, connectionLease.Connection, connectionLease.Transaction);

        var pId = cmd.Parameters.Add("@Id", SqlDbType.UniqueIdentifier);
        var pEnqueued = cmd.Parameters.Add("@EnqueuedAtUtc", SqlDbType.DateTimeOffset);
        var pType = cmd.Parameters.Add("@AssemblyQualifiedType", SqlDbType.NVarChar, 2048);
        var pPayload = cmd.Parameters.Add("@Payload", SqlDbType.NVarChar, -1);

        cmd.Prepare();

        foreach (var r in outboxRecords)
        {
            pId.Value = r.Id;
            pEnqueued.Value = r.EnqueuedAtUtc;
            pType.Value = r.AssemblyQualifiedType;
            pPayload.Value = r.Payload;

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<OutboxRecord[]> ReadNextBatch(int batchSize, CancellationToken cancellationToken)
    {
        var sql = $@"
SELECT TOP (@take)
    Id, EnqueuedAtUtc, Retries, AssemblyQualifiedType, Payload
FROM {QualifiedTableName()} WITH (READCOMMITTEDLOCK)
WHERE DispatchedAtUtc IS NULL AND FailedAtUtc IS NULL
ORDER BY Id ASC;";

        await using var connectionLease = await connectionLeaseProvider.Provide(cancellationToken);
        using var cmd = new SqlCommand(sql, connectionLease.Connection, connectionLease.Transaction);
        cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = batchSize });

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

    public Task IncrementRetries(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        return ExecuteUpdateByIdAsync("SET Retries = Retries + 1", outboxRecord.Id, _ => { }, cancellationToken);
    }

    public Task SetFailedAt(OutboxRecord outboxRecord, DateTimeOffset failedAtUtc, CancellationToken cancellationToken)
    {
        return ExecuteUpdateByIdAsync("SET FailedAtUtc = @value", outboxRecord.Id, cmd =>
            cmd.Parameters.Add(new SqlParameter("@value", SqlDbType.DateTimeOffset) { Value = failedAtUtc }),
            cancellationToken);
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
        await using var connectionLease = await connectionLeaseProvider.Provide(cancellationToken);
        using var cmd = new SqlCommand(sql, connectionLease.Connection, connectionLease.Transaction);
        addParameters(cmd);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private string QualifiedTableName()
        => $"{Bracket(sqlOutboxConfiguration.SchemaName)}.{Bracket(sqlOutboxConfiguration.TableName)}";

    private static string Bracket(string identifier)
        => $"[{identifier.Replace("[", string.Empty).Replace("]", string.Empty)}]";
}
