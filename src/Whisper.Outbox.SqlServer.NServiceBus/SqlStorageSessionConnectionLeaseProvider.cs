using Microsoft.Data.SqlClient;
using NServiceBus.Persistence.Sql;

namespace Whisper.Outbox.SqlServer.NServiceBus;
internal sealed class SqlStorageSessionConnectionLeaseProvider(ISqlStorageSession sqlStorageSession) : IConnectionLeaseProvider
{
    public ValueTask<ConnectionLease?> Provide(CancellationToken cancellationToken)
    {
        // NServiceBus only opens its storage session while it handles a message. Events stored in any other scope
        // (a plain HTTP request, for one) find a session without a connection, and the store then uses its own.
        if (sqlStorageSession.Connection is not { } connection)
            return ValueTask.FromResult<ConnectionLease?>(null);

        // The casts stay hard: a session on another ADO.NET provider must fail loudly rather than quietly lose the
        // shared transaction.
        return ValueTask.FromResult<ConnectionLease?>(new ConnectionLease((SqlConnection)connection, (SqlTransaction?)sqlStorageSession.Transaction));
    }
}
