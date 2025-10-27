using Microsoft.Data.SqlClient;
using NServiceBus.Persistence.Sql;

namespace Whisper.Outbox.SqlServer.NServiceBus;
internal sealed class SqlStorageSessionConnectionLeaseProvider(ISqlStorageSession sqlStorageSession) : IConnectionLeaseProvider
{
    public ValueTask<IConnectionLease> Provide(CancellationToken cancellationToken)
    {
        var connection = (SqlConnection)sqlStorageSession.Connection;
        var transaction = (SqlTransaction)sqlStorageSession.Transaction;
        var connectionLease = new ConnectionLease(connection, transaction);
        return ValueTask.FromResult<IConnectionLease>(connectionLease);
    }

    private sealed class ConnectionLease(SqlConnection sqlConnection, SqlTransaction sqlTransaction) : IConnectionLease
    {
        public SqlConnection Connection => sqlConnection;

        public SqlTransaction? Transaction => sqlTransaction;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}