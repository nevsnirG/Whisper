using Microsoft.Data.SqlClient;

namespace Hermes.Outbox.SqlServer;
internal sealed class SqlOutboxConfigurationConnectionLeaseProvider(SqlOutboxConfiguration sqlOutboxConfiguration) : IConnectionLeaseProvider
{
    public async ValueTask<IConnectionLease> Provide(CancellationToken cancellationToken)
    {
        var sqlConnection = new SqlConnection(sqlOutboxConfiguration.ConnectionString);
        await sqlConnection.OpenAsync(cancellationToken);
        return new ConnectionLease(sqlConnection);
    }

    private sealed class ConnectionLease(SqlConnection sqlConnection) : IConnectionLease
    {
        public SqlConnection Connection => sqlConnection;

        public SqlTransaction? Transaction => null;

        public ValueTask DisposeAsync()
        {
            return Connection.DisposeAsync();
        }
    }
}