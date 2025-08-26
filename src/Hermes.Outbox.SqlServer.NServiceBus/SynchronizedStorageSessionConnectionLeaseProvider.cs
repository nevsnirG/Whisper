using Microsoft.Data.SqlClient;
using NServiceBus.Persistence;

namespace Hermes.Outbox.SqlServer.NServiceBus;
internal sealed class SynchronizedStorageSessionConnectionLeaseProvider(ISynchronizedStorageSession synchronizedStorageSession) : IConnectionLeaseProvider
{
    public ValueTask<IConnectionLease> Provide(CancellationToken cancellationToken)
    {
        var sqlSession = synchronizedStorageSession.SqlPersistenceSession();
        var connection = (SqlConnection)sqlSession.Connection;
        var transaction = (SqlTransaction)sqlSession.Transaction;
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