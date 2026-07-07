using Microsoft.Data.SqlClient;
using NServiceBus.Persistence.Sql;

namespace Whisper.Outbox.SqlServer.NServiceBus;
internal sealed class SqlStorageSessionConnectionLeaseProvider(ISqlStorageSession sqlStorageSession) : IConnectionLeaseProvider
{
    public ValueTask<ConnectionLease> Provide(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new ConnectionLease((SqlConnection)sqlStorageSession.Connection, (SqlTransaction)sqlStorageSession.Transaction));
    }
}
