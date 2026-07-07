using Microsoft.Data.SqlClient;
using System.Data;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer;

/// <summary>
/// Singleton management store with zero dependencies on the ambient scoped providers.
/// Opens its own connection per operation from a connection string with Enlist=false,
/// making it immune to host unit-of-work middleware and ambient transactions.
/// </summary>
internal sealed class SqlOutboxManagementStore(SqlOutboxConfiguration sqlOutboxConfiguration) : IOutboxManagementStore
{
    private const int MinPageSize = 1;
    private const int MaxPageSize = 200;

    private readonly string _connectionString =
        new SqlConnectionStringBuilder(sqlOutboxConfiguration.ConnectionString) { Enlist = false }.ConnectionString;

    public async Task<OutboxFailedPage> GetFailed(int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);

        var sql = $@"
SELECT COUNT_BIG(*) FROM {QualifiedTableName()} WHERE FailedAtUtc IS NOT NULL;

SELECT Id, EnqueuedAtUtc, FailedAtUtc, Retries, AssemblyQualifiedType, LastError, LastErrorAtUtc
FROM {QualifiedTableName()}
WHERE FailedAtUtc IS NOT NULL
ORDER BY FailedAtUtc DESC, Id ASC
OFFSET @offset ROWS FETCH NEXT @fetch ROWS ONLY;";

        await using var connection = await OpenConnection(cancellationToken);
        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add(new SqlParameter("@offset", SqlDbType.Int) { Value = (page - 1) * pageSize });
        cmd.Parameters.Add(new SqlParameter("@fetch", SqlDbType.Int) { Value = pageSize });

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var totalCount = reader.GetInt64(0);

        await reader.NextResultAsync(cancellationToken);
        var summaries = new List<OutboxRecordSummary>(pageSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new OutboxRecordSummary(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return new OutboxFailedPage([.. summaries], totalCount);
    }

    public async Task<OutboxRecord?> Get(Guid id, CancellationToken cancellationToken)
    {
        var sql = $@"
SELECT Id, EnqueuedAtUtc, DispatchedAtUtc, FailedAtUtc, Retries, AssemblyQualifiedType, Payload, LastError, LastErrorAtUtc, NextRetryAtUtc
FROM {QualifiedTableName()}
WHERE Id = @id;";

        await using var connection = await OpenConnection(cancellationToken);
        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new OutboxRecord
        {
            Id = reader.GetGuid(0),
            EnqueuedAtUtc = reader.GetFieldValue<DateTimeOffset>(1),
            DispatchedAtUtc = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            FailedAtUtc = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            Retries = reader.GetInt32(4),
            AssemblyQualifiedType = reader.GetString(5),
            Payload = reader.GetString(6),
            LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
            LastErrorAtUtc = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            NextRetryAtUtc = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        };
    }

    public async Task<bool> Retry(Guid id, CancellationToken cancellationToken)
    {
        var sql = $@"
UPDATE {QualifiedTableName()}
SET FailedAtUtc = NULL, Retries = 0, NextRetryAtUtc = NULL
WHERE Id = @id AND FailedAtUtc IS NOT NULL;";

        return await ExecuteNonQuery(sql, cmd =>
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id }),
            cancellationToken) > 0;
    }

    public async Task<long> RetryAll(CancellationToken cancellationToken)
    {
        var sql = $@"
UPDATE {QualifiedTableName()}
SET FailedAtUtc = NULL, Retries = 0, NextRetryAtUtc = NULL
WHERE FailedAtUtc IS NOT NULL;";

        return await ExecuteNonQuery(sql, _ => { }, cancellationToken);
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken)
    {
        var sql = $@"
DELETE FROM {QualifiedTableName()}
WHERE Id = @id AND FailedAtUtc IS NOT NULL;";

        return await ExecuteNonQuery(sql, cmd =>
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id }),
            cancellationToken) > 0;
    }

    private async Task<int> ExecuteNonQuery(string sql, Action<SqlCommand> addParameters, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnection(cancellationToken);
        using var cmd = new SqlCommand(sql, connection);
        addParameters(cmd);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenConnection(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private string QualifiedTableName()
        => $"{SqlIdentifier.Bracket(sqlOutboxConfiguration.SchemaName)}.{SqlIdentifier.Bracket(sqlOutboxConfiguration.TableName)}";
}
