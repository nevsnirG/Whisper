using Microsoft.Data.SqlClient;
using System.Data;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.SqlServer;

/// <summary>
/// The worker lock as a session-owned application lock (sp_getapplock). It lives on its own connection, taken from
/// the configured connection string, for as long as a batch is processed. A session that ends for any reason (a
/// crash, a dropped connection) releases the lock on the server, so no lease has to expire first.
/// </summary>
internal sealed class SqlOutboxWorkerLock(SqlOutboxConfiguration sqlOutboxConfiguration) : IOutboxWorkerLock
{
    // sp_getapplock resource names are limited to 255 characters.
    private const int MaxLockResourceLength = 255;
    private const int ReleaseTimeoutSeconds = 5;

    // Enlist=false: the lock must never become part of an ambient transaction of the host.
    private readonly string _connectionString =
        new SqlConnectionStringBuilder(sqlOutboxConfiguration.ConnectionString) { Enlist = false }.ConnectionString;

    private readonly string _resource = LockResource(
        SqlIdentifier.Strip(sqlOutboxConfiguration.SchemaName), SqlIdentifier.Strip(sqlOutboxConfiguration.TableName));

    public async Task<IAsyncDisposable?> TryAcquire(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        var acquired = false;
        try
        {
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = @"
DECLARE @result INT;
EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0;
SELECT @result;";
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, MaxLockResourceLength) { Value = _resource });

            // 0 = granted. -1 = the timeout of 0 ran out, so another session holds it. Other negatives are errors:
            // cancelled (-2), deadlock victim (-3), parameter or call error (-999).
            var result = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
            if (result < -1)
                throw new InvalidOperationException($"Could not take the outbox worker lock '{_resource}' (sp_getapplock returned {result}).");

            acquired = result >= 0;
            return acquired ? new HeldLock(connection, _resource) : null;
        }
        finally
        {
            if (!acquired)
                await connection.DisposeAsync();
        }
    }

    // A truncated name can only make two outboxes share a lock, which just makes their workers take turns.
    private static string LockResource(string rawSchemaName, string rawTableName)
    {
        var resource = $"Whisper.Outbox.Worker:{rawSchemaName}.{rawTableName}";
        return resource.Length <= MaxLockResourceLength ? resource : resource[..MaxLockResourceLength];
    }

    private sealed class HeldLock(SqlConnection connection, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';";
                command.CommandTimeout = ReleaseTimeoutSeconds;
                command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, MaxLockResourceLength) { Value = resource });
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                // The session owns the lock, so the connection must not go back into the pool while it may still
                // hold it. Clearing the pool closes the connection for real, which ends the session and the lock.
                SqlConnection.ClearPool(connection);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
