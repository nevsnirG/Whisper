using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer;

internal sealed class SqlOutboxStore(SqlOutboxConfiguration sqlOutboxConfiguration, IConnectionLeaseProvider connectionLeaseProvider) : IOutboxStore
{
    private const int BatchSize = 10;

    public async Task Add(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        var table = QualifyTableName(sqlOutboxConfiguration.SchemaName, sqlOutboxConfiguration.TableName);
        var sql = $@"
INSERT INTO {table}
(Id, EnqueuedAtUtc, AssemblyQualifiedType, Payload)
VALUES (@Id, @EnqueuedAtUtc, @AssemblyQualifiedType, @Payload);";

        await using var connectionLease = await connectionLeaseProvider.Provide(cancellationToken);
        using var cmd = new SqlCommand(sql, connectionLease.Connection, connectionLease.Transaction);

        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = outboxRecord.Id });
        cmd.Parameters.Add(new SqlParameter("@EnqueuedAtUtc", SqlDbType.DateTimeOffset) { Value = outboxRecord.EnqueuedAtUtc });
        cmd.Parameters.Add(new SqlParameter("@AssemblyQualifiedType", SqlDbType.NVarChar, 2048) { Value = outboxRecord.AssemblyQualifiedType });
        cmd.Parameters.Add(new SqlParameter("@Payload", SqlDbType.NVarChar, -1) { Value = outboxRecord.Payload });

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task Add(OutboxRecord[] outboxRecords, CancellationToken cancellationToken)
    {
        var table = QualifyTableName(sqlOutboxConfiguration.SchemaName, sqlOutboxConfiguration.TableName);
        var sql = $@"
INSERT INTO {table}
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

    public async Task<OutboxRecord[]> ReadNextBatch(CancellationToken cancellationToken)
    {
        var table = QualifyTableName(sqlOutboxConfiguration.SchemaName, sqlOutboxConfiguration.TableName);
        var sql = $@"
SELECT TOP (@take)
    Id, EnqueuedAtUtc, AssemblyQualifiedType, Payload
FROM {table} WITH (READCOMMITTEDLOCK)
WHERE DispatchedAtUtc IS NULL
ORDER BY Id ASC;";

        await using var connectionLease = await connectionLeaseProvider.Provide(cancellationToken);
        using var cmd = new SqlCommand(sql, connectionLease.Connection, connectionLease.Transaction);
        cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = BatchSize });

        var list = new List<OutboxRecord>(BatchSize);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new OutboxRecord
            {
                Id = reader.GetGuid(0),
                EnqueuedAtUtc = reader.GetFieldValue<DateTimeOffset>(1),
                AssemblyQualifiedType = reader.GetString(2),
                Payload = reader.GetString(3),
            });
        }

        return [.. list];
    }

    public async Task SetDispatchedAt(OutboxRecord outboxRecord, DateTimeOffset dispatchedAtUtc, CancellationToken cancellationToken)
    {
        var table = QualifyTableName(sqlOutboxConfiguration.SchemaName, sqlOutboxConfiguration.TableName);

        var sb = new StringBuilder();
        sb.Append($@"
UPDATE {table} 
SET DispatchedAtUtc = @now
WHERE Id = @id;");

        await using var connectionLease = await connectionLeaseProvider.Provide(cancellationToken);
        using var cmd = new SqlCommand(sb.ToString(), connectionLease.Connection, connectionLease.Transaction);
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTimeOffset) { Value = dispatchedAtUtc });
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = outboxRecord.Id });

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QualifyTableName(string schemaName, string tableName)
    {
        return $"{Bracket(schemaName)}.{Bracket(tableName)}";
    }

    private static string Bracket(string identifier)
        => $"[{identifier.Replace("[", string.Empty).Replace("]", string.Empty)}]";
}