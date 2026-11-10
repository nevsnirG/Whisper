using Microsoft.Data.SqlClient;
using System.Data;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer;

/// <summary>
/// Storing events (<see cref="Add(OutboxRecord, CancellationToken)"/> and its batch overload) is the one thing that
/// belongs to the host's unit of work, so it is the only operation that asks the <see cref="IConnectionLeaseProvider"/>.
/// Every other operation is the worker's: it runs outside any unit of work, on a connection this store opens from
/// the configured connection string and disposes itself.
/// </summary>
internal sealed class SqlOutboxStore(SqlOutboxConfiguration sqlOutboxConfiguration, IConnectionLeaseProvider? connectionLeaseProvider) : IOutboxStore
{
    public async Task Add(OutboxRecord outboxRecord, CancellationToken cancellationToken)
    {
        var sql = $@"
INSERT INTO {QualifiedTableName()}
(Id, EnqueuedAtUtc, AssemblyQualifiedType, Payload)
VALUES (@Id, @EnqueuedAtUtc, @AssemblyQualifiedType, @Payload);";

        await using var leaseScope = await AcquireLease(cancellationToken);
        using var cmd = new SqlCommand(sql, leaseScope.Connection, leaseScope.Transaction);
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = outboxRecord.Id });
        cmd.Parameters.Add(new SqlParameter("@EnqueuedAtUtc", SqlDbType.DateTimeOffset) { Value = outboxRecord.EnqueuedAtUtc });
        cmd.Parameters.Add(new SqlParameter("@AssemblyQualifiedType", SqlDbType.NVarChar, 2048) { Value = outboxRecord.AssemblyQualifiedType });
        cmd.Parameters.Add(new SqlParameter("@Payload", SqlDbType.NVarChar, -1) { Value = outboxRecord.Payload });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task Add(OutboxRecord[] outboxRecords, CancellationToken cancellationToken)
    {
        var sql = $@"
INSERT INTO {QualifiedTableName()}
(Id, EnqueuedAtUtc, AssemblyQualifiedType, Payload)
VALUES (@Id, @EnqueuedAtUtc, @AssemblyQualifiedType, @Payload);";

        await using var leaseScope = await AcquireLease(cancellationToken);
        // On its own connection Whisper inserts the batch in a local transaction, so the batch is saved
        // entirely or not at all. A connection enlisted in an ambient TransactionScope is already atomic
        // (and cannot start a local transaction); a leased connection follows the host's transaction.
        await using var ownTransaction = leaseScope.OwnsConnection && !EnlistsInAmbientTransaction()
            ? (SqlTransaction)await leaseScope.Connection.BeginTransactionAsync(cancellationToken)
            : null;
        using var cmd = new SqlCommand(sql, leaseScope.Connection, ownTransaction ?? leaseScope.Transaction);

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

        if (ownTransaction is not null)
            await ownTransaction.CommitAsync(cancellationToken);
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

        await using var connection = await OpenOwnConnection(cancellationToken);
        using var cmd = new SqlCommand(sql, connection);
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
        return UpdateById("SET DispatchedAtUtc = @value", outboxRecord.Id, cmd =>
            cmd.Parameters.Add(new SqlParameter("@value", SqlDbType.DateTimeOffset) { Value = dispatchedAtUtc }),
            cancellationToken);
    }

    public Task ScheduleRetry(OutboxRecord outboxRecord, OutboxFailure failure, DateTimeOffset? nextRetryAtUtc, CancellationToken cancellationToken)
    {
        return UpdateById(
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
        return UpdateById(
            "SET FailedAtUtc = @failedAtUtc, LastError = @lastError, LastErrorAtUtc = @lastErrorAtUtc",
            outboxRecord.Id,
            cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@failedAtUtc", SqlDbType.DateTimeOffset) { Value = failure.OccurredAtUtc });
                AddFailureParameters(cmd, failure);
            },
            cancellationToken);
    }

    public async Task<int> DeleteDispatchedBefore(DateTimeOffset dispatchedBeforeUtc, int maxRecords, CancellationToken cancellationToken)
    {
        // TOP bounds the locks one statement takes, so a large cleanup never escalates to a table lock.
        // RECOMPILE: which plan is right depends on how much has expired (a scan finds a batch at once in a
        // backlog; with nothing expired only the index avoids reading the whole table). So the plan is chosen
        // for the values of each run rather than reused from an earlier one. The statement runs a few times a
        // minute at most, which makes the compile cost irrelevant.
        var sql = $@"
DELETE TOP (@maxRecords) FROM {QualifiedTableName()}
WHERE DispatchedAtUtc IS NOT NULL AND DispatchedAtUtc < @dispatchedBeforeUtc
OPTION (RECOMPILE);";

        await using var connection = await OpenOwnConnection(cancellationToken);
        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add(new SqlParameter("@maxRecords", SqlDbType.Int) { Value = maxRecords });
        cmd.Parameters.Add(new SqlParameter("@dispatchedBeforeUtc", SqlDbType.DateTimeOffset) { Value = dispatchedBeforeUtc });
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddFailureParameters(SqlCommand cmd, OutboxFailure failure)
    {
        cmd.Parameters.Add(new SqlParameter("@lastError", SqlDbType.NVarChar, -1) { Value = failure.Error });
        cmd.Parameters.Add(new SqlParameter("@lastErrorAtUtc", SqlDbType.DateTimeOffset) { Value = failure.OccurredAtUtc });
    }

    private async Task UpdateById(string setClause, Guid id, Action<SqlCommand> addParameters, CancellationToken cancellationToken)
    {
        var sql = $@"
UPDATE {QualifiedTableName()}
{setClause}
WHERE Id = @id;";

        await using var connection = await OpenOwnConnection(cancellationToken);
        using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        addParameters(cmd);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // For storing events only: the host's connection when a provider leases one, this store's own otherwise.
    private async ValueTask<LeaseScope> AcquireLease(CancellationToken cancellationToken)
    {
        // A provider with no unit of work in this scope (a plain HTTP request next to message handlers, for one)
        // returns null.
        if (connectionLeaseProvider is not null && await connectionLeaseProvider.Provide(cancellationToken) is { } lease)
            return new LeaseScope(lease, OwnsConnection: false);

        return new LeaseScope(new ConnectionLease(await OpenOwnConnection(cancellationToken)), OwnsConnection: true);
    }

    private async Task<SqlConnection> OpenOwnConnection(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(sqlOutboxConfiguration.ConnectionString);
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

    // Whether a connection this store opens right now joins an ambient System.Transactions transaction: there has
    // to be one, and the connection string must not opt out of enlisting.
    private bool EnlistsInAmbientTransaction()
        => System.Transactions.Transaction.Current is not null
            && new SqlConnectionStringBuilder(sqlOutboxConfiguration.ConnectionString).Enlist;

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
